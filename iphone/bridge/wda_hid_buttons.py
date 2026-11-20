"""Acknowledged buttons, authenticated contact input and pasteboard events.

Reuse the existing pairing and runner startup. Run with
-I -B -u wda_hid_buttons.py LEGACY_BRIDGE_PATH [UDID] [BUNDLE_ID].
"""
import asyncio
import base64
import os
import runpy
import sys
import time
from pathlib import Path
from contextlib import asynccontextmanager


BUTTON_NAMES = {0x40: "home", 0x30: "lock", 0xE9: "volup", 0xEA: "voldown"}
_active_touch = None
_contact_point = None


def bridge_lost(reason):
    print("HID_BRIDGE_LOST", flush=True)
    if __name__ == "__main__":
        try:
            log = Path(os.environ.get("WAILINK_LOGS") or Path(__file__).parent) / "hid-errors.log"
            log.parent.mkdir(parents=True, exist_ok=True)
            with log.open("a", encoding="utf-8") as stream:
                stream.write(time.strftime("%Y-%m-%d %H:%M:%S ") + reason + "\n")
        except OSError:
            pass
        # A normal exit waits on the thread blocked reading stdin, which can keep a
        # dead bridge alive for hours. The owner restarts on exit, so leave now.
        sys.stdout.flush()
        os._exit(1)
    raise SystemExit(reason)


def guarded_touch(base):
    class GuardedTouch(base):
        async def send_touchscreen(self, *args, **kwargs):
            try:
                return await asyncio.wait_for(super().send_touchscreen(*args, **kwargs), 3)
            except Exception as error:
                bridge_lost("touch: " + type(error).__name__)

    return GuardedTouch


def acknowledged_hid(base):
    class AcknowledgedHid(base):
        async def send_button(self, usage_page, usage_code, state):
            # HID down/up values are provided by the installed protocol module.
            from pymobiledevice3.remote.core_device.hid_service import (
                HID_BUTTON_STATE_DOWN,
                HID_BUTTON_STATE_UP,
            )

            button = usage_page == 0x0C and usage_code in BUTTON_NAMES
            down = getattr(self, "_wai_button_down", None)
            released = button and state == HID_BUTTON_STATE_UP and down is not None and down[0] == usage_code
            if released:
                deadline = down[1] + (0.5 if usage_code == 0x30 else 0.05)
                # Windows' asyncio clock can wake early. Recheck the high-resolution
                # deadline instead of treating one sleep as proof of the hold time.
                while (remaining := deadline - time.perf_counter()) > 0:
                    await asyncio.sleep(remaining)
            try:
                await super().send_button(usage_page, usage_code, state)
            except Exception as error:
                # The legacy reader suppresses Exception and otherwise leaves a
                # live process with a dead HID loop. Exit so its owner can reconnect.
                bridge_lost("button: " + type(error).__name__)
            if button and state == HID_BUTTON_STATE_DOWN:
                self._wai_button_down = (usage_code, time.perf_counter())
            if released:
                self._wai_button_down = None
                # Allow the async HID handler to consume the release before ACK.
                await asyncio.sleep(0.1)
                print("HID_BUTTON_OK " + BUTTON_NAMES[usage_code], flush=True)

    return AcknowledgedHid


@asynccontextmanager
async def touch_transport(rsd):
    global _active_touch, _contact_point
    from pymobiledevice3.exceptions import DeviceVersionNotSupportedError
    from pymobiledevice3.remote.core_device import hid_service
    from pymobiledevice3.remote.core_device.display_service import require_video_stream_support

    try:
        require_video_stream_support(rsd)
    except DeviceVersionNotSupportedError:
        # Keep physical buttons alive, but tell the owner to use WDA for touch.
        async with hid_service.UniversalHIDServiceService(rsd) as touch:
            print("HID_TOUCH_UNAVAILABLE", flush=True)
            yield touch
    else:
        async with hid_service.touch_session(rsd) as touch:
            _active_touch = touch
            print("HID_TOUCH_READY", flush=True)
            print("HID_CONTACT_READY", flush=True)
            try:
                yield touch
            finally:
                try:
                    if _contact_point is not None:
                        from pymobiledevice3.remote.core_device.hid_service import TOUCHSCREEN_STATE_RELEASE
                        await touch.send_touchscreen(TOUCHSCREEN_STATE_RELEASE, *_contact_point)
                finally:
                    _contact_point = None
                    _active_touch = None


def supervised_input_loop(read_input):
    async def run(self, test_done, case_started):
        from pymobiledevice3.remote.core_device.pasteboard_service import (
            PasteboardMonitor, read_pasteboard,
        )

        def changed(content):
            if content.text is not None:
                encoded = base64.b64encode(content.text.encode("utf-8")).decode("ascii")
                print("HID_CLIPBOARD " + encoded, flush=True)

        async def read_clipboard():
            try:
                _, content = await asyncio.wait_for(read_pasteboard(self.lockdown), 3)
                if content.text is None:
                    print("HID_CLIPBOARD_EMPTY", flush=True)
                else:
                    changed(content)
                print("HID_CLIPBOARD_READ_OK", flush=True)
            except Exception:
                print("HID_CLIPBOARD_READ_FAILED", flush=True)

        loop = asyncio.get_running_loop()
        original_input = sys.stdin
        contact = None

        async def send_contact(command, x, y):
            global _contact_point
            nonlocal contact
            from pymobiledevice3.remote.core_device.hid_service import (
                TOUCHSCREEN_STATE_CONTACT, TOUCHSCREEN_STATE_RELEASE,
            )
            if _active_touch is None:
                bridge_lost("continuous contact unavailable")
            state = TOUCHSCREEN_STATE_RELEASE if command == "CONTACT_UP" else TOUCHSCREEN_STATE_CONTACT
            await _active_touch.send_touchscreen(state, x, y)
            contact = None if command == "CONTACT_UP" else (x, y)
            _contact_point = contact

        class ClipboardInput:
            def readline(self):
                while True:
                    line = original_input.readline()
                    parts = line.strip().split()
                    if len(parts) == 3 and parts[0] in ("CONTACT_DOWN", "CONTACT_MOVE", "CONTACT_UP"):
                        x, y = (max(0, min(65535, int(v))) for v in parts[1:])
                        # Preserve ordering and mouse-up coordinates. The legacy
                        # swipe path deliberately adds momentum/braking samples;
                        # those samples do not belong to a continuous contact.
                        asyncio.run_coroutine_threadsafe(send_contact(parts[0], x, y), loop).result(4)
                        continue
                    if line.strip() in ("VIDEO_OFF", "VIDEO_ON"):
                        asyncio.run_coroutine_threadsafe(set_video(line.strip() == "VIDEO_ON"), loop)
                        continue
                    if line.strip() == "QUIT":
                        # End the screen stream before the runner shuts down, so iOS
                        # releases the camera and microphone even if the owner kills us soon after.
                        try:
                            asyncio.run_coroutine_threadsafe(set_video(False), loop).result(10)
                        except Exception:
                            pass
                    if line.strip() != "CLIP_GET":
                        return line
                    asyncio.run_coroutine_threadsafe(read_clipboard(), loop)

        monitor = None
        try:
            # Clipboard changes use their own service, not the gesture/input queue.
            if hasattr(self, "lockdown"):
                monitor = PasteboardMonitor(self.lockdown, changed)
                await monitor.start()
                sys.stdin = ClipboardInput()
                print("HID_CLIPBOARD_READY", flush=True)
            await read_input(self, test_done, case_started)
            if not test_done.is_set():
                bridge_lost("input loop stopped before runner")
        finally:
            sys.stdin = original_input
            try:
                if contact is not None and _active_touch is not None:
                    await send_contact("CONTACT_UP", *contact)
            finally:
                if monitor is not None:
                    await monitor.stop()

    return run


async def watch_tunnel():
    """Exit once the Wi-Fi tunnel is torn down. A runner launch that was in flight
    when the link dropped can otherwise wait forever and never hand back control."""
    from pymobiledevice3.remote import userspace_tunnel

    while userspace_tunnel._active_tunnel is not None:
        await asyncio.sleep(1)
    try:
        bridge_lost("tunnel closed")
    except SystemExit:
        # A normal exit waits (up to 5 min) for the thread blocked on stdin;
        # nothing is left to clean up once the tunnel is gone.
        sys.stdout.flush()
        os._exit(1)


async def run_screen_video(rsd):
    """Hardware screen video (iOS 27+) from wda_video.py beside this file, if present.
    It must run in this process: iOS ends a second process's screen stream."""
    import importlib.util

    path = Path(__file__).with_name("wda_video.py")
    if not path.is_file():
        return
    spec = importlib.util.spec_from_file_location("wda_video", path)
    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except Exception as error:
        print("HID_VIDEO_FAILED " + type(error).__name__, flush=True)
        return
    await module.run_video(rsd)


async def ensure_developer_image(rsd):
    """After an iPhone restart its developer disk image is unmounted and the test services
    (testmanagerd) disappear. Mount it, then exit so the owner reconnects with the new service list."""
    services = (getattr(rsd, "peer_info", None) or {}).get("Services", {})
    if not services or "com.apple.dt.testmanagerd.remote" in services:
        return
    from pymobiledevice3.services.mobile_image_mounter import auto_mount

    print("HID_DDI_MOUNTING", flush=True)
    try:
        await auto_mount(rsd)
    except Exception as error:
        print("HID_DDI_FAILED " + (str(error) or type(error).__name__).replace("\n", " ")[:200], flush=True)
        return
    print("HID_DDI_MOUNTED", flush=True)
    bridge_lost("developer image mounted; reconnecting")


# While the screen stream runs, iOS treats the phone as observed remotely and blocks
# the camera and microphone (and with them Face ID). VIDEO_OFF ends the stream so they
# work again; the owner falls back to WDA screenshots. WAILINK_VIDEO_PAUSED=1 starts paused.
_video_rsd = None
_video_task = None
_video_lock = None


async def set_video(on):
    global _video_task, _video_lock
    if _video_lock is None:
        _video_lock = asyncio.Lock()
    async with _video_lock:
        task = _video_task
        if on:
            if _video_rsd is not None and (task is None or task.done()):
                _video_task = asyncio.create_task(run_screen_video(_video_rsd))
            print("HID_VIDEO_RESUMED", flush=True)
        else:
            _video_task = None
            if task is not None and not task.done():
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
            print("HID_VIDEO_PAUSED", flush=True)


def tunnel_watched_run(original_run):
    async def run(self, cfg, *args, **kwargs):
        global _video_rsd
        await ensure_developer_image(self.lockdown)
        tunnel = asyncio.create_task(watch_tunnel())
        _video_rsd = self.lockdown
        # Start video before the runner launch, which can take 10+ seconds.
        await set_video(os.environ.get("WAILINK_VIDEO_PAUSED") != "1")
        try:
            return await original_run(self, cfg, *args, **kwargs)
        finally:
            tunnel.cancel()
            await set_video(False)
            _video_rsd = None

    return run


def main():
    if len(sys.argv) < 2:
        raise SystemExit("Expected the existing WDA bridge path")
    from pymobiledevice3.services.dvt.testmanaged import xcuitest
    from pymobiledevice3.remote.core_device import hid_service

    xcuitest.IndigoHIDService = acknowledged_hid(xcuitest.IndigoHIDService)
    # Keep the original XCUITest stdin DOWN/MOVE/UP transport unchanged.
    # Only hardware-button acknowledgements and clipboard events are additive.
    xcuitest.XCUITestService._stdin_hid_loop = supervised_input_loop(
        xcuitest.XCUITestService._stdin_hid_loop
    )
    xcuitest.XCUITestService.run = tunnel_watched_run(xcuitest.XCUITestService.run)
    legacy = sys.argv.pop(1)
    sys.argv[0] = legacy
    print("HID_BUTTON_ACK_READY", flush=True)
    runpy.run_path(legacy, run_name="__main__")


if __name__ == "__main__":
    main()
