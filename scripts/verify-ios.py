#!/usr/bin/env python3
"""Install an iOS simulator app, require a native verification marker, then stop it."""
import argparse
from pathlib import Path
import subprocess
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--device', required=True)
p.add_argument('--app', type=Path, required=True)
p.add_argument('--app-id', required=True)
p.add_argument('--expect', required=True)
p.add_argument('--log', type=Path, required=True)
p.add_argument('--timeout', type=int, default=60)
a = p.parse_args()
base = ['xcrun', 'simctl']
def stop():
    subprocess.run(base + ['terminate', a.device, a.app_id], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
stop()
subprocess.run(base + ['install', a.device, str(a.app.resolve())], check=True)
a.log.parent.mkdir(parents=True, exist_ok=True)
with a.log.open('w') as log:
    process = subprocess.Popen(base + ['launch', '--console-pty', a.device, a.app_id], stdout=log, stderr=subprocess.STDOUT)
    try:
        deadline = time.monotonic() + a.timeout
        while time.monotonic() < deadline:
            output = a.log.read_text()
            if 'FAIL:' in output:
                raise SystemExit('Runtime failed; see ' + str(a.log))
            if a.expect in output:
                print('\n'.join(line[line.index('PASS:'):] for line in output.splitlines() if 'PASS:' in line))
                break
            if process.poll() is not None:
                raise SystemExit('Application exited before verification; see ' + str(a.log))
            time.sleep(1)
        else:
            raise SystemExit('Timed out waiting for native check; see ' + str(a.log))
    finally:
        stop()
        try: process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=10)
