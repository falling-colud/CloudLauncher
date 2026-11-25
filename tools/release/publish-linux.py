"""Signs a built Linux launcher release and publishes it to the launcher server over SSH.

usage: python tools/release/publish-linux.py <version> <CloudLauncher-<version>-linux-x64.tar.gz> <notes.txt>

Same settings as publish-client.py (environment variables or tools/release/release.local.json), same
key, same checks; the files go to the launcher folder's linux/ subfolder, which the server's
LinuxLauncherStore serves at /launcher/linux/* and /download/linux. The Windows feed is not touched.

Steps: sign the tarball and check the signature the way the launcher will; stage it as *.new and check
size and sha256 on the server before moving it into place (linux/packages/<sha256>.bin and
linux/package.bin); write linux/releases.json (newest first, one entry per version, at most 60); write
linux/latest.json last; then check the live server from outside.
"""
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SIGNER_PROJECT = os.path.join(HERE, 'signer', 'signer.csproj')
SIGNER_OUT = os.path.join(REPO, 'artifacts', 'signer')
KEEP_PACKAGES = 3
VERSION_RE = re.compile(r'^\d+(\.\d+){1,3}$')
REMOTE_DIR_RE = re.compile(r'^[A-Za-z0-9_./-]+$')
USER_AGENT = 'CloudLauncher-release/1.0'

SETTINGS = {
    'ssh_host': ('CL_RELEASE_SSH_HOST', 'sshHost'),
    'ssh_key': ('CL_RELEASE_SSH_KEY', 'sshKey'),
    'remote_dir': ('CL_RELEASE_REMOTE_DIR', 'remoteDir'),
    'public_url': ('CL_RELEASE_PUBLIC_URL', 'publicUrl'),
    'sign_key': ('CL_RELEASE_SIGN_KEY', 'signKey'),
    'sign_pem': ('CL_RELEASE_SIGN_PEM', 'signPem'),
}
REQUIRED = ('ssh_host', 'ssh_key', 'remote_dir', 'public_url')


def load_config():
    local = {}
    path = os.path.join(HERE, 'release.local.json')
    if os.path.exists(path):
        with open(path, encoding='utf-8') as f:
            local = json.load(f)
    config = {}
    for name, (env, key) in SETTINGS.items():
        value = os.environ.get(env) or local.get(key)
        config[name] = value.strip() if isinstance(value, str) and value.strip() else None
    missing = [SETTINGS[name][0] for name in REQUIRED if not config[name]]
    if missing:
        raise SystemExit('missing settings: ' + ', '.join(missing))
    if not REMOTE_DIR_RE.match(config['remote_dir']):
        raise SystemExit('the remote folder may only contain letters, digits, _ . / and -')
    if not config['public_url'].startswith('https://'):
        raise SystemExit('the public URL must start with https://')
    config['ssh_key'] = os.path.expanduser(config['ssh_key'])
    config['public_url'] = config['public_url'].rstrip('/')
    config['sign_key'] = config['sign_key'] or 'k1'
    if config['sign_pem']:
        config['sign_pem'] = os.path.expanduser(config['sign_pem'])
    return config


def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def ssh(cmd, check=True):
    r = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-i', CONFIG['ssh_key'], CONFIG['ssh_host'], cmd],
                       capture_output=True, text=True)
    if check and r.returncode != 0:
        raise SystemExit(f'ssh failed: {cmd}\n{r.stderr}')
    return r.stdout.strip()


def scp(local, remote):
    r = subprocess.run(['scp', '-o', 'BatchMode=yes', '-i', CONFIG['ssh_key'], local,
                        f'{CONFIG["ssh_host"]}:{remote}'], capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f'scp failed: {local}\n{r.stderr}')


def verify_remote(path, size, digest):
    got = ssh(f'stat -c %s {path}; sha256sum {path} | cut -d" " -f1').split()
    if got != [str(size), digest]:
        raise SystemExit(f'{path}: server has {got}, expected {[size, digest]}')


def build_signer():
    r = subprocess.run(['dotnet', 'build', SIGNER_PROJECT, '-c', 'Release', '-nologo', '-v', 'q', '-o', SIGNER_OUT],
                       capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit('the signer did not build:\n' + r.stdout + r.stderr)
    return os.path.join(SIGNER_OUT, 'cl-signer.dll')


def signer(dll, *args):
    return subprocess.run(['dotnet', dll, *args], capture_output=True, text=True)


def write_json(path, value):
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(value, f, ensure_ascii=False)


def get(url, timeout=60):
    req = urllib.request.Request(url, headers={'User-Agent': USER_AGENT})
    return urllib.request.urlopen(req, timeout=timeout)


CONFIG = load_config()
DIR = CONFIG['remote_dir'].rstrip('/') + '/linux'
PUBLIC = CONFIG['public_url']

version, tar_path, notes_path = sys.argv[1:4]
if not VERSION_RE.match(version):
    raise SystemExit(f'{version!r} is not a version like 1.2.3')
file_name = os.path.basename(tar_path)
if not re.match(r'^CloudLauncher-' + re.escape(version) + r'-linux-(x64|arm64)\.tar\.gz$', file_name):
    raise SystemExit(f'{file_name} is not named CloudLauncher-{version}-linux-<arch>.tar.gz')

size, digest = os.path.getsize(tar_path), sha(tar_path)
with open(notes_path, encoding='utf-8-sig') as f:
    notes = f.read().strip()
print(f'tarball {size} {digest}')

with tempfile.TemporaryDirectory() as tmp:
    dll = build_signer()
    sign_args = ['sign', '--zip', tar_path, '--version', version, '--key', CONFIG['sign_key']]
    if CONFIG['sign_pem']:
        sign_args += ['--pem', CONFIG['sign_pem']]
    r = signer(dll, *sign_args)
    if r.returncode != 0:
        raise SystemExit('signing failed:\n' + r.stderr)
    signed = json.loads(r.stdout)
    if signed['sha256'] != digest or signed['size'] != size:
        raise SystemExit('the signer hashed a different file than this script did')
    print('signed with', signed['keyId'])

    latest = {
        'version': version,
        'fileName': file_name,
        'size': size,
        'sha256': digest,
        'notes': notes,
        'releasedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
        'manifest': signed['manifest'],
        'signature': signed['signature'],
        'keyId': signed['keyId'],
    }
    latest_tmp = os.path.join(tmp, 'latest.json')
    write_json(latest_tmp, latest)
    r = signer(dll, 'verify', '--zip', tar_path, '--json', latest_tmp)
    if r.returncode != 0:
        raise SystemExit('latest.json to publish: the signature check failed\n' + r.stdout + r.stderr)
    print('latest.json to publish:', r.stdout.strip())

    ssh(f'mkdir -p {DIR}/packages')
    prev = json.loads(ssh(f'cat {DIR}/latest.json 2>/dev/null || echo "{{}}"') or '{}')
    prev_version = prev.get('version')
    print('previous', prev_version)
    if prev_version == version:
        raise SystemExit('that version is already published')
    backup_suffix = prev_version if isinstance(prev_version, str) and VERSION_RE.match(prev_version) else 'none'

    print('uploading', f'linux/packages/{digest}.bin')
    scp(tar_path, f'{DIR}/packages/{digest}.bin.new')
    verify_remote(f'{DIR}/packages/{digest}.bin.new', size, digest)
    ssh(f'mv {DIR}/packages/{digest}.bin.new {DIR}/packages/{digest}.bin')
    ssh(f'cp {DIR}/packages/{digest}.bin {DIR}/package.bin.new')
    verify_remote(f'{DIR}/package.bin.new', size, digest)
    ssh(f'if [ -e {DIR}/package.bin ]; then cp -a {DIR}/package.bin {DIR}/package.bin.bak-{backup_suffix}; fi'
        f' && mv {DIR}/package.bin.new {DIR}/package.bin')
    print('  in place, verified')

    rel = json.loads(ssh(f'cat {DIR}/releases.json 2>/dev/null || echo "[]"') or '[]')
    rel = [x for x in rel if x.get('version') != version]
    rel.insert(0, latest)
    rel = rel[:60]
    releases_tmp = os.path.join(tmp, 'releases.json')
    write_json(releases_tmp, rel)
    scp(releases_tmp, f'{DIR}/releases.json.new')
    ssh(f'python3 -c "import json;json.load(open(\'{DIR}/releases.json.new\'))" && mv {DIR}/releases.json.new {DIR}/releases.json')
    print('releases.json written', len(rel), 'entries')

    ssh(f'if [ -e {DIR}/latest.json ]; then cp -a {DIR}/latest.json {DIR}/latest.json.bak-{backup_suffix}; fi')
    scp(latest_tmp, f'{DIR}/latest.json.new')
    ssh(f'python3 -c "import json;json.load(open(\'{DIR}/latest.json.new\'))" && mv {DIR}/latest.json.new {DIR}/latest.json')
    print('latest.json written')

    ssh(f'cd {DIR}/packages && ls -1t -- *.bin | tail -n +{KEEP_PACKAGES + 1} | grep -v -x {digest}.bin'
        f' | xargs -r rm -f --', check=False)
    # Old package.bin backups are the size of a release each; keep only the newest two.
    ssh(f'cd {DIR} && ls -1t -- package.bin.bak-* 2>/dev/null | tail -n +3 | xargs -r rm -f --', check=False)

    problems = []
    with get(f'{PUBLIC}/launcher/linux/latest') as resp:
        live_raw = resp.read()
    live = json.loads(live_raw)
    print('live version', live.get('version'))
    if live.get('version') != version:
        problems.append(f'live version is {live.get("version")}')
    for field in ('sha256', 'manifest', 'signature', 'keyId'):
        if live.get(field) != latest[field]:
            problems.append(f'live {field} differs from what was published')
    live_tmp = os.path.join(tmp, 'live-latest.json')
    with open(live_tmp, 'wb') as f:
        f.write(live_raw)
    r = signer(dll, 'verify', '--zip', tar_path, '--json', live_tmp)
    print('live signature:', (r.stdout or r.stderr).strip())
    if r.returncode != 0:
        problems.append('the live latest.json does not verify')

    for route in (f'launcher/linux/download?sha256={digest}', 'launcher/linux/download', 'download/linux'):
        h = hashlib.sha256()
        with get(f'{PUBLIC}/{route}', timeout=600) as resp:
            for chunk in iter(lambda: resp.read(1 << 20), b''):
                h.update(chunk)
        ok = h.hexdigest() == digest
        print(f'download {route}:', 'sha ok' if ok else 'MISMATCH ' + h.hexdigest())
        if not ok:
            problems.append(f'{route} serves a different file')

if problems:
    print('\nPUBLISHED, BUT THE LIVE CHECKS FAILED:\n  ' + '\n  '.join(problems))
    sys.exit(1)
print('\nLinux release', version, 'is live and verified')
