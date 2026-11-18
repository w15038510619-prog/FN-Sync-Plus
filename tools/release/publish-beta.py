#!/usr/bin/env python3
"""Promote a signed UpdateTool Beta or Stable release from private staging into the HTTPS feed."""

import argparse
import base64
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


DEFAULT_ROOT = Path('/data/fnsync-updates')
DEFAULT_URL = 'https://dev.sk-fj.com:8081/fnsync/beta'
FIELDS = set('schema product channel arch version min_version released_at notes url sha256 '
             'size batch audience min_compatible_client signature'.split())


def require(condition, message):
    if not condition:
        raise ValueError(message)


def version(value):
    require(isinstance(value, str) and re.fullmatch(r'(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}', value),
            'Expected a numeric release version.')
    return tuple(map(int, value.split('.')))


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


@contextmanager
def publish_lock(private):
    with (private / 'publish.lock').open('a+b') as lock:
        if os.name == 'nt':
            import msvcrt
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        yield


def unique_object(pairs):
    result = dict(pairs)
    require(len(result) == len(pairs), 'Duplicate JSON field.')
    return result


def openssl_signature(raw_signature):
    raw = base64.b64decode(raw_signature, validate=True)
    require(len(raw) == 64, 'Expected an ECDSA P-256 P1363 signature.')
    encoded = b''
    for coordinate in (raw[:32], raw[32:]):
        coordinate = coordinate.lstrip(b'\x00') or b'\x00'
        if coordinate[0] & 0x80:
            coordinate = b'\x00' + coordinate
        encoded += b'\x02' + bytes([len(coordinate)]) + coordinate
    return b'\x30' + bytes([len(encoded)]) + encoded


def verify_manifest(raw, public_key, stage):
    # UpdateTool appends the signature as the last field of the exact canonical bytes it signs.
    match = re.fullmatch(rb'(\{.+),"signature":"([A-Za-z0-9+/=]+)"\}', raw)
    require(match is not None, 'Expected the original signed UpdateTool manifest bytes.')
    document = json.loads(raw.decode('utf-8'), object_pairs_hook=unique_object)
    require(set(document) == FIELDS, 'Unexpected manifest fields.')
    signature = stage / 'signature.der'
    signature.write_bytes(openssl_signature(match[2]))
    try:
        subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(public_key), '-signature', str(signature)],
                       input=match[1] + b'}', check=True)
    finally:
        signature.unlink()
    return document


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('pin', 'zip', 'manifest'))
    parser.add_argument('--root', type=Path, default=DEFAULT_ROOT)
    parser.add_argument('--base-url')
    parser.add_argument('--channel', choices=('beta', 'stable'), default='beta')
    parser.add_argument('--stage', required=True)
    parser.add_argument('--key-sha256', required=True)
    parser.add_argument('--version')
    parser.add_argument('--minimum-client')
    parser.add_argument('--size', type=int)
    parser.add_argument('--sha256')
    parser.add_argument('--manifest-sha256')
    args = parser.parse_args()
    args.base_url = args.base_url or f"https://dev.sk-fj.com:8081/fnsync/{args.channel}"

    require(re.fullmatch('[0-9a-f]{32}', args.stage), 'Invalid staging identifier.')
    require(re.fullmatch('[0-9a-f]{64}', args.key_sha256), 'Invalid public key fingerprint.')
    private = args.root / '.beta-staging'
    stage = private / args.stage
    feed = args.root / args.channel
    trusted = private / 'beta-update-public.pem'
    require(stage.is_dir() and feed.is_dir(), 'Feed and staging directories must exist.')
    require(stage.stat().st_dev == feed.stat().st_dev, 'Staging and feed must be on the same filesystem.')

    with publish_lock(private):
        if args.action == 'pin':
            upload = stage / 'public.upload'
            require(file_hash(upload) == args.key_sha256, 'Public key fingerprint mismatch.')
            subprocess.run(['openssl', 'pkey', '-pubin', '-in', str(upload), '-noout'], check=True)
            if not trusted.exists():
                os.link(upload, trusted)
            require(file_hash(trusted) == args.key_sha256, 'Existing Beta trust root differs.')
            print('Beta public key fingerprint verified.')
            return

        require(file_hash(trusted) == args.key_sha256, 'Pinned Beta public key fingerprint mismatch.')
        release = version(args.version)
        require(args.size and args.size > 0 and re.fullmatch('[0-9a-f]{64}', args.sha256 or ''),
                'Invalid package size or SHA-256.')
        name = f'FN-Sync-Plus-{args.version}-x64.zip'
        package = feed / name

        def matches_package(path):
            return path.is_file() and path.stat().st_size == args.size and file_hash(path) == args.sha256

        if args.action == 'zip':
            upload = stage / 'payload.upload'
            require(matches_package(upload), 'Staged ZIP size or SHA-256 mismatch.')
            if package.exists():
                require(matches_package(package), 'A different ZIP already exists for this release.')
            else:
                upload.chmod(0o644)
                os.link(upload, package)
            upload.unlink()
            print(f'ZIP available: {name} ({args.size} bytes, SHA-256 {args.sha256}).')
            return

        upload = stage / 'manifest.upload'
        require(upload.stat().st_size <= 256 * 1024, 'Update manifest is too large.')
        raw = upload.read_bytes()
        require(hashlib.sha256(raw).hexdigest() == args.manifest_sha256, 'Manifest transfer SHA-256 mismatch.')
        document = verify_manifest(raw, trusted, stage)
        require(document['schema'] == 1 and document['product'] == 'FN Sync Plus'
                and document['channel'] == args.channel and document['arch'] == 'x64'
                and document['version'] == args.version, 'Manifest identity or version mismatch.')
        require(document['min_version'] == document['min_compatible_client'] == args.minimum_client
                and version(args.minimum_client) < release, 'Manifest compatibility mismatch.')
        require(document['url'] == f'{args.base_url}/{name}' and document['size'] == args.size
                and document['sha256'].lower() == args.sha256 and matches_package(package),
                'Manifest URL, ZIP size or ZIP SHA-256 mismatch.')

        live = feed / 'manifest.json'
        if live.exists():
            previous = live.read_bytes()
            if previous == raw:
                print('This signed Beta manifest is already live.')
                return
            old_version = json.loads(previous, object_pairs_hook=unique_object).get('version')
            if old_version is not None:
                require(version(old_version) < release, 'The existing manifest is for the same or a later release.')
            history = private / ('manifest-' + hashlib.sha256(previous).hexdigest() + '.json')
            if not history.exists():
                os.link(live, history)
        upload.chmod(0o644)
        os.replace(upload, live)
        print(f'Signed {args.channel} manifest activated for {args.version}.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, TypeError, AttributeError, subprocess.CalledProcessError) as error:
        sys.exit(f'Publication refused: {error}')
