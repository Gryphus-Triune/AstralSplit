"""Regenerate the two embedded driver modules using CUDA Toolkit 12.9 NVRTC on Windows."""
import argparse
import ctypes as ct
import os
from pathlib import Path


def compile_module(library, source, arch, kind):
    pointer = ct.c_void_p
    program = pointer()

    def call(name, *args):
        status = getattr(library, name)(*args)
        if status:
            raise RuntimeError(f"{name} returned NVRTC error {status}")

    call("nvrtcCreateProgram", ct.byref(program), source, b"Galaxy.cu", 0, None, None)
    try:
        opts = (ct.c_char_p * 3)(b"--std=c++14", b"--fmad=false", b"--gpu-architecture=" + arch)
        result = library.nvrtcCompileProgram(program, len(opts), opts)
        if result:
            length = ct.c_size_t()
            call("nvrtcGetProgramLogSize", program, ct.byref(length))
            log = ct.create_string_buffer(length.value)
            call("nvrtcGetProgramLog", program, log)
            raise RuntimeError(log.value.decode(errors="replace"))
        length = ct.c_size_t()
        call("nvrtcGet" + kind + "Size", program, ct.byref(length))
        data = ct.create_string_buffer(length.value)
        call("nvrtcGet" + kind, program, data)
        return data.raw
    finally:
        call("nvrtcDestroyProgram", ct.byref(program))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bin", type=Path, default=Path(os.environ["ProgramFiles"]) /
                        "NVIDIA GPU Computing Toolkit/CUDA/v12.9/bin")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    source = (root / "Galaxy.cu").read_bytes()
    with os.add_dll_directory(str(args.bin)):
        lib = ct.WinDLL(str(args.bin / "nvrtc64_120_0.dll"))
        for arch, kind, name in [(b"sm_120", "CUBIN", "Galaxy.cubin"),
                                 (b"compute_52", "PTX", "Galaxy.ptx")]:
            (root / name).write_bytes(compile_module(lib, source, arch, kind))
