import argparse
import os
import sys
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", default="5000")
    args = parser.parse_args()

    executable_dir = Path(sys.executable).resolve().parent
    runtime_root = executable_dir.parent
    if not (runtime_root / "argos-data").exists() and (runtime_root.parent / "argos-data").exists():
        runtime_root = runtime_root.parent
    argos_data = runtime_root / "argos-data"
    argos_cache = runtime_root / "argos-cache"
    argos_config = runtime_root / "argos-config"
    argos_packages = argos_data / "argos-translate" / "packages"

    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    os.environ["XDG_DATA_HOME"] = str(argos_data)
    os.environ["XDG_CACHE_HOME"] = str(argos_cache)
    os.environ["XDG_CONFIG_HOME"] = str(argos_config)
    os.environ["ARGOS_PACKAGES_DIR"] = str(argos_packages)

    sys.argv = [
        "libretranslate",
        "--host",
        "127.0.0.1",
        "--port",
        str(args.port),
        "--load-only",
        "en,es",
        "--disable-web-ui",
    ]

    from libretranslate.main import main as libretranslate_main

    libretranslate_main()


if __name__ == "__main__":
    main()
