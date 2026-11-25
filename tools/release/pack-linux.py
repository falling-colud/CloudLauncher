"""Packs a published Linux launcher into the release tarball.

usage: python tools/release/pack-linux.py <publish dir> <version> <rid> <out.tar.gz>

The tarball has one top folder, cloudlauncher/, holding the single-file launcher ("cloudlauncher"),
install.sh, the icon and the licence files. It is written here rather than with a Windows tar so the
two programs carry their executable bits (0755) and everything else 0644, owned by root:root, with one
fixed time, so the same input always packs to the same bytes.
"""
import gzip
import io
import os
import sys
import tarfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

publish_dir, version, rid, out_path = sys.argv[1:5]
exe = os.path.join(publish_dir, 'cloudlauncher')
if not os.path.isfile(exe):
    raise SystemExit(f'no launcher at {exe}: publish first (tools/release/build-linux.ps1)')

files = [
    ('cloudlauncher', exe, 0o755),
    ('install.sh', os.path.join(HERE, 'linux', 'install.sh'), 0o755),
    ('cloudlauncher.png', os.path.join(REPO, 'CloudLauncher.Linux', 'Assets', 'cloudlauncher.png'), 0o644),
    ('LICENSE.txt', os.path.join(REPO, 'LICENSE'), 0o644),
    ('THIRD-PARTY-NOTICES.txt', os.path.join(REPO, 'THIRD-PARTY-NOTICES.txt'), 0o644),
    ('README.txt', os.path.join(HERE, 'linux', 'README.txt'), 0o644),
]
for _, src, _ in files:
    if not os.path.isfile(src):
        raise SystemExit(f'missing {src}')

MTIME = 1735689600  # 2025-01-01T00:00:00Z: fixed, so a re-pack of the same files is byte-identical


def add_dir(tar, name):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    info.mode = 0o755
    info.mtime = MTIME
    info.uid = info.gid = 0
    info.uname = info.gname = 'root'
    tar.addfile(info)


raw = io.BytesIO()
with tarfile.open(fileobj=raw, mode='w', format=tarfile.PAX_FORMAT) as tar:
    add_dir(tar, 'cloudlauncher')
    for name, src, mode in files:
        with open(src, 'rb') as fh:
            data = fh.read()
        if name.endswith('.sh'):
            # A script saved on Windows must not reach Linux with CRLF line ends.
            data = data.replace(b'\r\n', b'\n')
        info = tarfile.TarInfo('cloudlauncher/' + name)
        info.size = len(data)
        info.mode = mode
        info.mtime = MTIME
        info.uid = info.gid = 0
        info.uname = info.gname = 'root'
        tar.addfile(info, io.BytesIO(data))

with open(out_path, 'wb') as out:
    # mtime=0 in the gzip header too, for the same reason.
    with gzip.GzipFile(filename='', mode='wb', fileobj=out, mtime=0, compresslevel=9) as gz:
        gz.write(raw.getvalue())

print(f'{out_path}: {os.path.getsize(out_path)} bytes ({version}, {rid})')
