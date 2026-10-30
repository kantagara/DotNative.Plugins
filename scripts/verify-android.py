#!/usr/bin/env python3
"""Install an APK, launch its actual activity and require a native verification marker."""
import argparse
from pathlib import Path
import re
import subprocess
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--adb', default='adb')
p.add_argument('--device', required=True)
p.add_argument('--apk', type=Path, required=True)
p.add_argument('--app-id', required=True)
p.add_argument('--expect', required=True)
p.add_argument('--log', type=Path, required=True)
a = p.parse_args()
if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.]+', a.app_id):
    p.error('Invalid application identifier')
base = [a.adb, '-s', a.device]
def call(*args):
    return subprocess.check_output(base + list(args), text=True, stderr=subprocess.STDOUT)
call('shell', 'am', 'force-stop', a.app_id)
print(call('install', '-r', str(a.apk.resolve())))
activity = call('shell', 'cmd', 'package', 'resolve-activity', '--brief', a.app_id).splitlines()[-1]
if not activity.startswith(a.app_id + '/'):
    raise SystemExit('No launch activity found: ' + activity)
print(call('shell', 'am', 'start', '-W', '-n', activity))
deadline = time.monotonic() + 60
output = ''
while time.monotonic() < deadline:
    try:
        pid = call('shell', 'pidof', a.app_id).strip()
        output = call('logcat', '-d', '--pid=' + pid)
    except subprocess.CalledProcessError:
        raise SystemExit('Application exited before verification completed')
    a.log.parent.mkdir(parents=True, exist_ok=True)
    a.log.write_text(output)
    if 'FAIL:' in output or 'FATAL EXCEPTION' in output:
        raise SystemExit('Runtime failed; see ' + str(a.log))
    if a.expect in output:
        print('\n'.join(line[line.index('PASS:'):] for line in output.splitlines() if 'PASS:' in line))
        break
    time.sleep(1)
else:
    raise SystemExit('Timed out waiting for native check; see ' + str(a.log))
