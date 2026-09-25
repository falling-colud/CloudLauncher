"""Signs a built client release and publishes it to the launcher server over SSH.

usage: python tools/release/publish-client.py <version> <zip> <installer.exe> <notes.txt>

Where to publish comes from environment variables, or from tools/release/release.local.json (kept
out of git; start from release.example.json). An environment variable wins over the file:

  CL_RELEASE_SSH_HOST     user@host of the server             (json: sshHost)
  CL_RELEASE_SSH_KEY      SSH private key file                (json: sshKey)
  CL_RELEASE_REMOTE_DIR   the server's launcher data folder   (json: remoteDir)
  CL_RELEASE_PUBLIC_URL   the server's public https address   (json: publicUrl)
  CL_RELEASE_SIGN_KEY     key id to sign with, default k1     (json: signKey)
  CL_RELEASE_SIGN_PEM     PEM file for a key kept as PEM      (json: signPem)

Steps, in order: sign the package and check the signature locally; stage the binaries as *.new and
check their size and sha256 on the server before moving them into place (packages/<sha256>.bin,
package.bin, installer.bin); write releases.json (newest first, one entry per version, at most 60);
write latest.json last, so a client polling mid-publish never sees metadata for a package that is
not there yet; then check the live server from outside.
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

# setting -> (environment variable, release.local.json key)
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
        raise SystemExit('missing settings: ' + ', '.join(missing) + '\nset them as environment variables '
                         'or in tools/release/release.local.json (see release.example.json)')
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


def signer_verify(dll, zip_path, json_path, what):
    r = signer(dll, 'verify', '--zip', zip_path, '--json', json_path)
    if r.returncode != 0:
        raise SystemExit(f'{what}: the signature check failed\n{r.stdout}{r.stderr}')
    print(f'{what}: {r.stdout.strip()}')


def write_json(path, value):
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(value, f, ensure_ascii=False)


def upload_verified(local, remote, size, digest):
    """scp to remote.new, check it on the server, then move it into place."""
    scp(local, f'{remote}.new')
    verify_remote(f'{remote}.new', size, digest)
    ssh(f'mv {remote}.new {remote}')


CONFIG = load_config()
DIR = CONFIG['remote_dir']
PUBLIC = CONFIG['public_url']

version, zip_path, inst_path, notes_path = sys.argv[1:5]
if not VERSION_RE.match(version):
    raise SystemExit(f'{version!r} is not a version like 1.2.3')

zip_size, zip_sha = os.path.getsize(zip_path), sha(zip_path)
inst_size, inst_sha = os.path.getsize(inst_path), sha(inst_path)
with open(notes_path, encoding='utf-8') as f:
    notes = f.read().strip()
print(f'zip {zip_size} {zip_sha}\ninstaller {inst_size} {inst_sha}')

with tempfile.TemporaryDirectory() as tmp:
    # 1. Sign locally, and check the signature the way a launcher will, before anything is uploaded.
    dll = build_signer()
    sign_args = ['sign', '--zip', zip_path, '--version', version, '--key', CONFIG['sign_key']]
    if CONFIG['sign_pem']:
        sign_args += ['--pem', CONFIG['sign_pem']]
    r = signer(dll, *sign_args)
    if r.returncode != 0:
        raise SystemExit('signing failed:\n' + r.stderr)
    signed = json.loads(r.stdout)
    if signed['sha256'] != zip_sha or signed['size'] != zip_size:
        raise SystemExit('the signer hashed a different file than this script did')
    print('signed with', signed['keyId'])

    latest = {
        'version': version,
        'fileName': f'CloudLauncher-{version}.zip',
        'size': zip_size,
        'sha256': zip_sha,
        'notes': notes,
        'releasedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
        'installerFileName': f'CloudLauncher-Setup-{version}.exe',
        'installerSize': inst_size,
        'installerSha256': inst_sha,
        'manifest': signed['manifest'],
        'signature': signed['signature'],
        'keyId': signed['keyId'],
    }
    latest_tmp = os.path.join(tmp, 'latest.json')
    write_json(latest_tmp, latest)
    signer_verify(dll, zip_path, latest_tmp, 'latest.json to publish')

    prev = json.loads(ssh(f'cat {DIR}/latest.json 2>/dev/null || echo "{{}}"') or '{}')
    prev_version = prev.get('version')
    print('previous', prev_version)
    if prev_version == version:
        raise SystemExit('that version is already published')
    # Only ever used in backup file names on the server.
    backup_suffix = prev_version if isinstance(prev_version, str) and VERSION_RE.match(prev_version) else 'unknown'

    # 2. Binaries, staged and verified on the server. The package is uploaded once under its hash
    #    (what signed launchers download) and copied on the server to package.bin (what older ones
    #    download).
    ssh(f'mkdir -p {DIR}/packages')
    print('uploading', f'packages/{zip_sha}.bin')
    upload_verified(zip_path, f'{DIR}/packages/{zip_sha}.bin', zip_size, zip_sha)
    print('  in place, verified')

    print('copying to package.bin')
    ssh(f'cp {DIR}/packages/{zip_sha}.bin {DIR}/package.bin.new')
    verify_remote(f'{DIR}/package.bin.new', zip_size, zip_sha)
    ssh(f'if [ -e {DIR}/package.bin ]; then cp -a {DIR}/package.bin {DIR}/package.bin.bak-{backup_suffix}; fi'
        f' && mv {DIR}/package.bin.new {DIR}/package.bin')
    print('  in place, verified')

    print('uploading installer.bin')
    scp(inst_path, f'{DIR}/installer.bin.new')
    verify_remote(f'{DIR}/installer.bin.new', inst_size, inst_sha)
    ssh(f'if [ -e {DIR}/installer.bin ]; then cp -a {DIR}/installer.bin {DIR}/installer.bin.bak-{backup_suffix}; fi'
        f' && mv {DIR}/installer.bin.new {DIR}/installer.bin')
    print('  in place, verified')

    # 3. releases.json: newest first, dedup, cap 60.
    rel = json.loads(ssh(f'cat {DIR}/releases.json 2>/dev/null || echo "[]"') or '[]')
    rel = [r for r in rel if r.get('version') != version]
    rel.insert(0, latest)
    rel = rel[:60]
    releases_tmp = os.path.join(tmp, 'releases.json')
    write_json(releases_tmp, rel)
    ssh(f'if [ -e {DIR}/releases.json ]; then cp -a {DIR}/releases.json {DIR}/releases.json.bak-{backup_suffix}; fi')
    scp(releases_tmp, f'{DIR}/releases.json.new')
    ssh(f'python3 -c "import json;json.load(open(\'{DIR}/releases.json.new\'))" && mv {DIR}/releases.json.new {DIR}/releases.json')
    print('releases.json written', len(rel), 'entries')

    # 4. latest.json, last: from here on launchers see the new release.
    ssh(f'if [ -e {DIR}/latest.json ]; then cp -a {DIR}/latest.json {DIR}/latest.json.bak-{backup_suffix}; fi')
    scp(latest_tmp, f'{DIR}/latest.json.new')
    ssh(f'python3 -c "import json;json.load(open(\'{DIR}/latest.json.new\'))" && mv {DIR}/latest.json.new {DIR}/latest.json')
    print('latest.json written')

    # 5. Keep the newest few packages by hash, so a launcher that read the previous latest.json a
    #    moment ago can still fetch the package it verified.
    ssh(f'cd {DIR}/packages && ls -1t -- *.bin | tail -n +{KEEP_PACKAGES + 1} | grep -v -x {zip_sha}.bin'
        f' | xargs -r rm -f --', check=False)
    print('packages kept:', ssh(f'ls -1t {DIR}/packages', check=False).replace('\n', ' '))

    # 6. Check the live server from outside, the way a launcher sees it.
    problems = []
    with urllib.request.urlopen(f'{PUBLIC}/launcher/latest', timeout=30) as resp:
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
    r = signer(dll, 'verify', '--zip', zip_path, '--json', live_tmp)
    print('live signature:', (r.stdout or r.stderr).strip())
    if r.returncode != 0:
        problems.append('the live latest.json does not verify')

    for route in (f'launcher/download?sha256={zip_sha}', 'launcher/download'):
        h = hashlib.sha256()
        with urllib.request.urlopen(f'{PUBLIC}/{route}', timeout=600) as resp:
            for chunk in iter(lambda: resp.read(1 << 20), b''):
                h.update(chunk)
        ok = h.hexdigest() == zip_sha
        print(f'download {route}:', 'sha ok' if ok else 'MISMATCH ' + h.hexdigest())
        if not ok:
            problems.append(f'{route} serves a different file')

if problems:
    print('\nPUBLISHED, BUT THE LIVE CHECKS FAILED:\n  ' + '\n  '.join(problems))
    sys.exit(1)
print('\nrelease', version, 'is live and verified')
