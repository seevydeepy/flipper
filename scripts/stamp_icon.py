import argparse
import ctypes
import struct
from ctypes import wintypes
from pathlib import Path

RT_ICON = 3
RT_GROUP_ICON = 14
LANG_NEUTRAL = 0


def makeintresource(value: int):
    return ctypes.c_wchar_p(value)


def parse_ico(path: Path):
    data = path.read_bytes()
    _, ico_type, count = struct.unpack_from("<HHH", data, 0)
    if ico_type != 1 or count == 0:
        raise ValueError("not an icon")
    images = []
    offset = 6
    for _ in range(count):
        width, height, colors, _reserved, planes, bitcount, size, img_offset = struct.unpack_from(
            "<BBBBHHII", data, offset
        )
        images.append((width, height, colors, planes, bitcount, data[img_offset : img_offset + size]))
        offset += 16
    return images


def group_resource(images):
    parts = [struct.pack("<HHH", 0, 1, len(images))]
    for index, (width, height, colors, planes, bitcount, payload) in enumerate(images, start=1):
        parts.append(struct.pack("<BBBBHHIH", width, height, colors, 0, planes, bitcount, len(payload), index))
    return b"".join(parts)


def stamp(exe: Path, ico: Path):
    images = parse_ico(ico)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    begin = kernel32.BeginUpdateResourceW
    begin.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
    begin.restype = wintypes.HANDLE
    update = kernel32.UpdateResourceW
    update.argtypes = [
        wintypes.HANDLE,
        wintypes.LPCWSTR,
        wintypes.LPCWSTR,
        wintypes.WORD,
        wintypes.LPVOID,
        wintypes.DWORD,
    ]
    update.restype = wintypes.BOOL
    end = kernel32.EndUpdateResourceW
    end.argtypes = [wintypes.HANDLE, wintypes.BOOL]
    end.restype = wintypes.BOOL

    handle = begin(str(exe), False)
    if not handle:
        raise OSError(ctypes.get_last_error(), "BeginUpdateResourceW")

    try:
        for index, (_w, _h, _c, _p, _b, payload) in enumerate(images, start=1):
            buf = ctypes.create_string_buffer(payload, len(payload))
            if not update(handle, makeintresource(RT_ICON), makeintresource(index), LANG_NEUTRAL, buf, len(payload)):
                raise OSError(ctypes.get_last_error(), f"UpdateResource icon {index}")
        group = group_resource(images)
        group_buf = ctypes.create_string_buffer(group, len(group))
        if not update(handle, makeintresource(RT_GROUP_ICON), makeintresource(1), LANG_NEUTRAL, group_buf, len(group)):
            raise OSError(ctypes.get_last_error(), "UpdateResource group")
        if not end(handle, False):
            raise OSError(ctypes.get_last_error(), "EndUpdateResourceW")
    except Exception:
        end(handle, True)
        raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("exe")
    parser.add_argument("ico")
    args = parser.parse_args()
    stamp(Path(args.exe), Path(args.ico))
    print(f"stamped {args.ico} onto {args.exe}")


if __name__ == "__main__":
    main()
