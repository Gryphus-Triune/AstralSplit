"""Create a verified portable archive from a complete, staged AstralSplit release."""
import argparse
import hashlib
import re
import tempfile
from pathlib import Path
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile
import ctypes
from ctypes import wintypes


def product_version(exe):
    api = ctypes.WinDLL("version", use_last_error=True)
    ignored = wintypes.DWORD()
    size = api.GetFileVersionInfoSizeW(str(exe), ctypes.byref(ignored))
    data = ctypes.create_string_buffer(size)
    address = ctypes.c_void_p()
    length = wintypes.UINT()
    if not size or not api.GetFileVersionInfoW(str(exe), 0, size, data):
        raise ctypes.WinError(ctypes.get_last_error())
    if not api.VerQueryValueW(data, "\\", ctypes.byref(address), ctypes.byref(length)) or length.value < 52:
        raise ValueError("Missing executable version resource")
    info = ctypes.cast(address, ctypes.POINTER(wintypes.DWORD))
    return ".".join(str(value) for value in (info[2] >> 16, info[2] & 65535, info[3] >> 16, info[3] & 65535))


def package(root, output):
    version = product_version(root / "AstralSplit.exe")
    source = root / "source"
    manifest = ElementTree.parse(source / "App.manifest").find("{urn:schemas-microsoft-com:asm.v1}assemblyIdentity")
    if manifest is None or manifest.get("version") != version:
        raise ValueError("Executable and manifest versions differ")
    guide = root / "使用说明.md"
    title = guide.read_text(encoding="utf-8-sig").splitlines()[0]
    if not re.search(r"\s" + re.escape(version.rsplit(".", 1)[0]) + r"$", title):
        raise ValueError("Executable and guide versions differ")
    files = [root / name for name in ("AstralSplit.exe", "AstralSplit.Graphics.dll",
             "engine/LICENSE", "engine/COPYING", "LICENSE", "README.md", "THIRD_PARTY_NOTICES.md", "THIRD_PARTY_LICENSES.txt",
             "Build-Release.ps1", "Install-Engine.ps1", "下载分流引擎.cmd", "预览界面.cmd",
             "兼容绘制.cmd", "实时磨砂对照.cmd", "使用说明.md")]
    files.extend(sorted((root / "tools").glob("*.*")))
    files.extend(sorted(p for p in source.rglob("*") if p.is_file() and not {"bin", "obj", "__pycache__"}.intersection(p.relative_to(source).parts)))
    digests = {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).digest() for p in files}
    if digests["source/AstralSplit.Graphics.dll"] != digests["AstralSplit.Graphics.dll"]:
        raise ValueError("Source and runtime graphics libraries differ")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=output.parent, delete=False) as handle:
        staging = Path(handle.name)
    try:
        with ZipFile(staging, "w", ZIP_DEFLATED, compresslevel=6) as archive:
            for p in files:
                archive.write(p, p.relative_to(root).as_posix())
        with ZipFile(staging) as archive:
            for member, expected in digests.items():
                if hashlib.sha256(archive.read(member)).digest() != expected:
                    raise ValueError("Archive content mismatch: " + member)
        staging.replace(output)
    finally:
        staging.unlink(missing_ok=True)
    print(f"{version} | {len(digests)} verified files | {output}")


if __name__ == "__main__":
    args = argparse.ArgumentParser()
    args.add_argument("release", type=Path)
    args.add_argument("archive", type=Path)
    options = args.parse_args()
    package(options.release.resolve(), options.archive.resolve())
