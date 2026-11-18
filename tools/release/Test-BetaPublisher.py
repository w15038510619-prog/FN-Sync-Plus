"""Run the CentOS publication protocol against a signed release in a temporary directory."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
from urllib.parse import urlsplit


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def check(condition, description):
    if not condition:
        raise RuntimeError(description)
    print('PASS ' + description)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('manifest', type=Path)
    parser.add_argument('public_key', type=Path)
    args = parser.parse_args()
    manifest = args.manifest.read_bytes()
    fields = json.loads(manifest)
    url = urlsplit(fields['url'])
    base_url = fields['url'].rsplit('/', 1)[0]
    zip_name = url.path.rsplit('/', 1)[1]
    package = args.manifest.parent / zip_name

    with TemporaryDirectory(prefix='fnsync-beta-publisher-') as scratch:
        root = Path(scratch)
        stage_id = 'b' * 32
        stage = root / '.beta-staging' / stage_id
        feed = root / fields['channel']
        stage.mkdir(parents=True)
        feed.mkdir()
        placeholder = b'{"version":"0.0.0"}'
        live = feed / 'manifest.json'
        live.write_bytes(placeholder)
        publisher = Path(__file__).with_name('publish-beta.py')
        common = ['--channel', fields['channel'], '--root', str(root), '--base-url', base_url, '--stage', stage_id,
                  '--key-sha256', sha256(args.public_key)]
        artifact = ['--version', fields['version'], '--minimum-client', fields['min_compatible_client'],
                    '--size', str(fields['size']), '--sha256', fields['sha256'].lower()]

        def run(action, flags=(), expected_error=None):
            result = subprocess.run([sys.executable, str(publisher), action] + common + list(flags),
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            if expected_error is None and result.returncode != 0:
                raise RuntimeError(result.stdout)
            if expected_error is not None and (result.returncode == 0 or expected_error.lower() not in result.stdout.lower()):
                raise RuntimeError(result.stdout)

        def promote_manifest(contents, expected_error=None):
            staged = stage / 'manifest.upload'
            staged.write_bytes(contents)
            run('manifest', artifact + ['--manifest-sha256', sha256(staged)], expected_error)

        shutil.copyfile(args.public_key, stage / 'public.upload')
        run('pin')
        promote_manifest(manifest, 'Manifest URL, ZIP size or ZIP SHA-256 mismatch.')
        check(live.read_bytes() == placeholder, 'signed manifest waits for the finished ZIP')

        staged_zip = stage / 'payload.upload'
        shutil.copyfile(package, staged_zip)
        with staged_zip.open('r+b') as stream:
            first = stream.read(1)[0]
            stream.seek(0)
            stream.write(bytes([first ^ 1]))
        run('zip', artifact, 'Staged ZIP size or SHA-256 mismatch.')
        check(not (feed / zip_name).exists(), 'same-size corrupted ZIP leaves no public file')

        shutil.copyfile(package, staged_zip)
        run('zip', artifact)
        check(sha256(feed / zip_name) == sha256(package) and live.read_bytes() == placeholder,
              'complete ZIP is public while the old manifest still serves')

        tampered = manifest.replace(b'"product":"FN Sync Plus"', b'"product":"FN Sync Test"')
        if tampered == manifest:
            raise RuntimeError('Unable to construct a valid JSON with an invalid signature.')
        promote_manifest(tampered, 'Verification failure')
        check(live.read_bytes() == placeholder, 'tampered manifest preserves the old feed')

        promote_manifest(manifest)
        history = root / '.beta-staging' / ('manifest-' + hashlib.sha256(placeholder).hexdigest() + '.json')
        check(live.read_bytes() == manifest and history.read_bytes() == placeholder,
              'verified manifest is activated and the prior file is preserved')
        shutil.copyfile(package, staged_zip)
        run('zip', artifact)
        promote_manifest(manifest)
        check(live.read_bytes() == manifest, 'same verified release can be retried')


if __name__ == '__main__':
    main()
