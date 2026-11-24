from pathlib import Path

from yellowfox_platform import camoufox_home


def configure_camoufox_home() -> Path:
    install_dir = camoufox_home()

    import camoufox.pkgman as pkgman

    pkgman.INSTALL_DIR = install_dir
    return install_dir
