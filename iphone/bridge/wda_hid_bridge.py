import asyncio
import json
import logging
import os
import sys
from pathlib import Path
from typing import Optional

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

ARG_UDID = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] != "auto" else None
ARG_BUNDLE_ID = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "auto" else None
CACHE_PATH = Path(os.environ.get("WAILINK_DATA") or Path(__file__).parent) / "wda_runner_cache.json"


async def resolve_udid(explicit_udid: Optional[str]) -> Optional[str]:
    if explicit_udid:
        return explicit_udid
    try:
        from pymobiledevice3 import usbmux
        usb_devs = await usbmux.list_devices()
        if usb_devs:
            return usb_devs[0].serial
    except Exception:
        pass
    try:
        from pymobiledevice3.pair_records import iter_remote_pair_records_by_identifier
        for identifier, _, _ in iter_remote_pair_records_by_identifier():
            return identifier
    except Exception:
        pass
    return None


async def resolve_test_config(rsd, udid: Optional[str], explicit_bundle_id: Optional[str]):
    from pymobiledevice3.services.dvt.testmanaged.xcuitest import TestConfig
    from pymobiledevice3.services.installation_proxy import InstallationProxyService

    cache_data = {}
    if CACHE_PATH.exists():
        try:
            cache_data = json.loads(CACHE_PATH.read_text(encoding="utf-8"))
        except Exception:
            cache_data = {}

    dev_key = udid or "default"
    cached_app = cache_data.get(dev_key) if isinstance(cache_data, dict) else None
    if isinstance(cached_app, dict) and cached_app.get("CFBundleIdentifier") and cached_app.get("Path"):
        if not explicit_bundle_id or cached_app.get("CFBundleIdentifier") == explicit_bundle_id:
            return TestConfig(runner_app_info=cached_app)

    if isinstance(cache_data, dict) and cache_data.get("CFBundleIdentifier") and cache_data.get("Path"):
        if not explicit_bundle_id or cache_data.get("CFBundleIdentifier") == explicit_bundle_id:
            return TestConfig(runner_app_info=cache_data)

    async with InstallationProxyService(lockdown=rsd) as install_svc:
        if explicit_bundle_id:
            apps = await install_svc.get_apps(bundle_identifiers=[explicit_bundle_id])
            if explicit_bundle_id in apps:
                app_info = apps[explicit_bundle_id]
                _save_cache(dev_key, app_info)
                return TestConfig(runner_app_info=app_info)

        user_apps = await install_svc.get_apps(application_type="User")
        for bid, info in user_apps.items():
            if "WebDriverAgentRunner" in bid or str(info.get("CFBundleExecutable", "")).startswith("WebDriverAgentRunner"):
                _save_cache(dev_key, info)
                return TestConfig(runner_app_info=info)

    fallback_id = explicit_bundle_id or "com.facebook.WebDriverAgentRunner.xctrunner"
    cfg = await TestConfig.create_for(rsd, runner_bundle_id=fallback_id)
    _save_cache(dev_key, cfg.runner_app_info)
    return cfg


def _save_cache(dev_key: str, app_info: dict) -> None:
    try:
        existing = {}
        if CACHE_PATH.exists():
            try:
                loaded = json.loads(CACHE_PATH.read_text(encoding="utf-8"))
                if isinstance(loaded, dict) and "CFBundleIdentifier" not in loaded:
                    existing = loaded
            except Exception:
                existing = {}
        existing[dev_key] = app_info
        CACHE_PATH.write_text(json.dumps(existing, default=str), encoding="utf-8")
    except Exception:
        pass


async def main() -> None:
    from pymobiledevice3.remote.userspace_tunnel import UserspaceRsdTunnel
    from pymobiledevice3.services.dvt.testmanaged.xcuitest import XCUITestService

    udid = await resolve_udid(ARG_UDID)
    async with UserspaceRsdTunnel(serial=udid) as rsd:
        cfg = await resolve_test_config(rsd, udid or getattr(rsd, "udid", None), ARG_BUNDLE_ID)
        await XCUITestService(rsd).run(cfg)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
