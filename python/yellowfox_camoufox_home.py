from pathlib import Path


def configure_camoufox_home() -> Path:
    install_dir = Path(__file__).resolve().parent / ".camoufox"

    import camoufox.pkgman as pkgman

    pkgman.INSTALL_DIR = install_dir
    return install_dir
