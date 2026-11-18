"""Hardware screen video for WAI Link (iOS 27 and later).

Runs inside the WDA bridge process because iOS keeps only one screen stream
per device: a second stream from another process ends this one within seconds.
The stream is HEVC; it is decoded here with PyAV and served as MJPEG on
127.0.0.1:9101 in the same multipart format WebDriverAgent uses on port 9100,
so the iPhone view can read either. The phone's sound (AAC-ELD) is decoded to raw
48 kHz stereo PCM on 127.0.0.1:9102 while a listener is connected. Status lines:
HID_VIDEO_READY, HID_VIDEO_UNSUPPORTED, HID_VIDEO_FAILED <reason>, HID_AUDIO_FAILED <reason>.
"""
import asyncio
import contextlib
import logging
import queue
import threading
from fractions import Fraction

PORT = 9101
AUDIO_PORT = 9102
# AAC-ELD 48 kHz stereo with 480-sample frames. The phone's own cookie (F8 E6 40 00)
# leaves frameLengthFlag at 512; FFmpeg needs the bit set to decode 480-sample frames.
AUDIO_ASC = bytes([0xF8, 0xE6, 0x50, 0x00])
MAX_HEIGHT = 1600
JPEG_QSCALE = 4  # MJPEG quantizer: 2 is near lossless, higher is smaller and softer
BOUNDARY = b"WaiLinkFrame"
RETRY_SECONDS = 10

log = logging.getLogger("wailink.video")


class FrameHub:
    """Latest JPEG, published from the decoder thread, awaited by MJPEG clients."""

    def __init__(self, loop):
        self._loop = loop
        self.jpeg = None
        self.seq = 0
        self.published_at = 0.0
        self._changed = asyncio.Event()

    def publish_threadsafe(self, jpeg):
        self._loop.call_soon_threadsafe(self._publish, jpeg)

    def _publish(self, jpeg):
        self.jpeg = jpeg
        self.seq += 1
        self.published_at = self._loop.time()
        changed, self._changed = self._changed, asyncio.Event()
        changed.set()

    async def newer_than(self, seq):
        while self.seq <= seq:
            await self._changed.wait()
        return self.jpeg, self.seq


def _annexb(au):
    """4-byte length-prefixed NAL units (the server's framing) to Annex-B start codes."""
    out = bytearray()
    i = 0
    while i + 4 <= len(au):
        size = int.from_bytes(au[i:i + 4], "big")
        out += b"\x00\x00\x00\x01" + au[i + 4:i + 4 + size]
        i += 4 + size
    return bytes(out)


def _content_crop(frame):
    """Under fast motion the phone encodes a shrunk screen into the top-left corner
    with flat gray (128) padding. Return the content box to stretch back, or None."""
    from PIL import Image, ImageChops

    fw, fh = frame.width, frame.height
    dh = 344
    dw = max(8, round(dh * fw / fh))
    small = frame.to_image(width=dw, height=dh)
    mask = ImageChops.difference(small, Image.new("RGB", (dw, dh), (128, 128, 128)))
    box = mask.convert("L").point(lambda v: 255 if v > 6 else 0).getbbox()
    if not box:
        return None
    cw, ch = round(box[2] / dw * fw), round(box[3] / dh * fh)
    if fw * 0.2 < cw < fw * 0.92 and fh * 0.2 < ch < fh * 0.92:
        return 0, 0, cw, ch
    return None


class JpegEncoder:
    """FFmpeg's MJPEG encoder on YUV frames: several times faster than an RGB round trip through PIL."""

    def __init__(self):
        self._codec = None
        self._size = None

    def encode(self, frame):
        import av

        fw, fh = frame.width, frame.height
        oh = min(MAX_HEIGHT, fh) // 2 * 2
        ow = max(2, round(oh * fw / fh) // 2 * 2)
        crop = _content_crop(frame)
        if crop:
            from PIL import Image

            # Rare (fast motion only): stretch the shrunk corner back to full size.
            image = frame.to_image().crop(crop).resize((ow, oh), Image.BILINEAR)
            frame = av.VideoFrame.from_image(image)
        if self._codec is None or self._size != (ow, oh):
            codec = av.CodecContext.create("mjpeg", "w")
            codec.width, codec.height = ow, oh
            codec.pix_fmt = "yuvj420p"
            codec.time_base = Fraction(1, 1000)
            codec.options = {"qmin": str(JPEG_QSCALE), "qmax": str(JPEG_QSCALE)}
            codec.open()
            self._codec, self._size = codec, (ow, oh)
        scaled = frame.reformat(width=ow, height=oh, format="yuvj420p", interpolation="BILINEAR")
        return b"".join(bytes(packet) for packet in self._codec.encode(scaled))


def _decode_worker(work, hub, server):
    import av

    codec = None
    jpeg = JpegEncoder()
    while True:
        msg = work.get()
        if msg is None:
            return
        kind, au = msg[4], msg[5:]
        if codec is None or kind == 2:
            codec = av.CodecContext.create("hevc", "r")
            with contextlib.suppress(Exception):
                codec.thread_type = "SLICE"
        data = _annexb(au)
        if kind != 1 and au[4:5] and (au[4] >> 1) & 0x3F != 32:
            # Key frame without parameter sets: supply the latest ones seen on the stream.
            params = [server._vps_nal, server._sps_nal, server._pps_nal]
            if all(params):
                data = b"".join(b"\x00\x00\x00\x01" + p for p in params) + data
        try:
            frames = codec.decode(av.Packet(data))
        except Exception as error:
            log.warning("video decode failed (%s); waiting for the next key frame", error)
            codec = None
            continue
        if not frames or not work.empty():
            continue  # a newer frame is already queued; only show the latest
        try:
            hub.publish_threadsafe(jpeg.encode(frames[-1]))
        except Exception:
            log.exception("video frame conversion failed")


async def _serve_client(reader, writer, hub):
    with contextlib.suppress(Exception):
        writer.get_extra_info("socket").setsockopt(6, 1, 1)  # TCP_NODELAY
    try:
        await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 3)
        writer.write(
            b"HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=" + BOUNDARY
            + b"\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"
        )
        # Send the current frame only if it is fresh, so a stalled stream reads as no
        # video (and the viewer falls back) instead of a frozen picture.
        fresh = asyncio.get_running_loop().time() - hub.published_at < 1.0
        seq = hub.seq - 1 if fresh else hub.seq
        while True:
            jpeg, seq = await hub.newer_than(seq)
            writer.write(
                b"--" + BOUNDARY + b"\r\nContent-Type: image/jpeg\r\nContent-Length: "
                + str(len(jpeg)).encode() + b"\r\n\r\n" + jpeg + b"\r\n"
            )
            await writer.drain()
    except (asyncio.IncompleteReadError, asyncio.TimeoutError, ConnectionError):
        pass
    finally:
        writer.close()


class AudioHub:
    """Decoded phone sound (s16le, 48 kHz, stereo) fanned out to local listeners."""

    def __init__(self):
        self.listeners = set()

    def broadcast(self, pcm):
        for q in list(self.listeners):
            if q.full():
                with contextlib.suppress(asyncio.QueueEmpty):
                    q.get_nowait()  # a slow listener skips ahead instead of lagging
            q.put_nowait(pcm)


def _audio_receiver(server, hub):
    """Replaces the library's macOS-only audio receiver: same RTP bookkeeping (needed
    for the receiver reports that keep the session alive), decoded with FFmpeg."""
    import av

    def new_decoder():
        codec = av.CodecContext.create("aac", "r")
        codec.extradata = AUDIO_ASC
        codec.sample_rate = 48000  # FFmpeg's ELD path fails ("internal bug") without these
        codec.layout = "stereo"
        return codec, av.AudioResampler(format="s16", layout="stereo", rate=48000)

    async def receive(transport):
        codec, resampler = new_decoder()
        errors = 0
        while True:
            try:
                data = await transport.recv(65535)
            except (OSError, asyncio.CancelledError):
                return
            if len(data) < 12 or 64 <= (data[1] & 0x7F) <= 95:  # RTCP
                continue
            server._audio_rtp_packets_received += 1
            seq = int.from_bytes(data[2:4], "big")
            cur = server._audio_rtp_highest_seq
            cycles = (cur >> 16) & 0xFFFF
            if seq < (cur & 0xFFFF) and (cur & 0xFFFF) - seq > 0x8000:
                cycles = (cycles + 1) & 0xFFFF
            new = (cycles << 16) | seq
            if cur == 0 or ((new - cur) & 0xFFFFFFFF) < 0x80000000:
                server._audio_rtp_highest_seq = new
            header = 12 + (data[0] & 0x0F) * 4
            if data[0] & 0x10:
                if header + 4 > len(data):
                    continue
                header += 4 + int.from_bytes(data[header + 2:header + 4], "big") * 4
            payload = data[header:]
            if not payload:
                continue
            try:
                for frame in codec.decode(av.Packet(payload)):
                    for out in resampler.resample(frame):
                        hub.broadcast(bytes(out.planes[0])[: out.samples * 4])
                errors = 0
            except Exception:
                errors += 1
                if errors >= 5:
                    codec, resampler = new_decoder()
                    errors = 0

    return receive


async def _serve_audio(reader, writer, server, hub):
    """Raw PCM to one listener. The phone's audio stream runs only while someone listens;
    after the last one leaves, the phone ends it about 20 s later (no more receiver reports)."""
    q = asyncio.Queue(maxsize=50)
    try:
        await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 3)
        await asyncio.wait_for(server._ensure_audio_stream(), 10)
        writer.write(b"HTTP/1.1 200 OK\r\nContent-Type: audio/L16;rate=48000;channels=2\r\nConnection: close\r\n\r\n")
        hub.listeners.add(q)
        while True:
            writer.write(await q.get())
            await writer.drain()
    except (asyncio.IncompleteReadError, asyncio.TimeoutError, ConnectionError):
        pass
    except Exception as error:
        print("HID_AUDIO_FAILED " + (str(error) or type(error).__name__).replace("\n", " ")[:200], flush=True)
    finally:
        hub.listeners.discard(q)
        writer.close()
        if not hub.listeners:
            with contextlib.suppress(Exception):
                await server._stop_audio_stream()


async def _revive(server, screen_stream):
    """The stall watchdog gives up after a few restarts (for example while the phone
    is locked). Let it try again every 30 s so video returns after an unlock."""
    loop = asyncio.get_running_loop()
    while True:
        await asyncio.sleep(5)
        if (server._consecutive_restarts >= screen_stream._MAX_STALL_RESTARTS
                and loop.time() - server._last_restart_t > 30):
            server._consecutive_restarts = 0


async def _stream_once(rsd, screen_stream):
    from pymobiledevice3.remote.core_device.display_service import DisplayService

    loop = asyncio.get_running_loop()
    server = screen_stream.ScreenStreamServer(rsd, audio_default_on=False)
    hub = FrameHub(loop)
    work = queue.Queue(maxsize=6)
    aus = asyncio.Queue(maxsize=64)
    state = screen_stream._SubState()
    audio = AudioHub()
    server._audio_udp_recv = _audio_receiver(server, audio)
    tasks = []
    listener = audio_listener = None
    try:
        await asyncio.wait_for(server._ensure_fresh_stream(), 15)
        state.mark_needs_key(loop.time())
        state.reset_on_key = True
        server._subscribers[aus] = state
        server._request_recovery_idr(reason="wailink-start")
        threading.Thread(target=_decode_worker, args=(work, hub, server), daemon=True,
                         name="wailink-video-decode").start()
        tasks = [asyncio.create_task(coro) for coro in (
            server._stall_watchdog(), server._decoder_refresh_loop(), _revive(server, screen_stream))]
        listener = await asyncio.start_server(
            lambda r, w: _serve_client(r, w, hub), "127.0.0.1", PORT)
        audio_listener = await asyncio.start_server(
            lambda r, w: _serve_audio(r, w, server, audio), "127.0.0.1", AUDIO_PORT)
        print("HID_VIDEO_READY", flush=True)
        while True:
            msg = await aus.get()
            try:
                work.put_nowait(msg)
            except queue.Full:
                # Decoder fell behind: drop the backlog and resume at the next key frame.
                with contextlib.suppress(queue.Empty):
                    while True:
                        work.get_nowait()
                state.mark_needs_key(loop.time())
                server._request_recovery_idr(reason="decoder-behind")
    finally:
        server._subscribers.pop(aus, None)
        for task in tasks:
            task.cancel()
        for server_socket in (listener, audio_listener):
            if server_socket is not None:
                server_socket.close()
        with contextlib.suppress(BaseException):
            await asyncio.wait_for(server._stop_audio_stream(), 3)
        with contextlib.suppress(Exception):
            work.put_nowait(None)
        with contextlib.suppress(BaseException):
            await asyncio.wait_for(server._stop_active_stream(), 3)
        with contextlib.suppress(BaseException):
            await asyncio.wait_for(DisplayService.stop_all_streams(rsd), 3)


async def run_video(rsd):
    """Serve the phone's screen until cancelled. Never raises into the bridge."""
    try:
        from packaging.version import Version
        from pymobiledevice3.remote.core_device import screen_stream
        from pymobiledevice3.remote.core_device.display_service import VIDEO_STREAM_MIN_VERSION

        if Version(rsd.product_version) < VIDEO_STREAM_MIN_VERSION:
            print("HID_VIDEO_UNSUPPORTED", flush=True)
            return
    except Exception as error:
        print("HID_VIDEO_FAILED " + type(error).__name__, flush=True)
        return
    while True:
        try:
            await _stream_once(rsd, screen_stream)
        except asyncio.CancelledError:
            raise
        except Exception as error:
            print("HID_VIDEO_FAILED " + (str(error) or type(error).__name__).replace("\n", " ")[:200], flush=True)
        await asyncio.sleep(RETRY_SECONDS)
