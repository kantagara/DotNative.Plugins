#!/usr/bin/env python3
"""Install an APK, launch its actual activity and require a native verification marker."""
import argparse
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--adb', default='adb')
p.add_argument('--device', required=True)
p.add_argument('--apk', type=Path, required=True)
p.add_argument('--app-id', required=True)
p.add_argument('--expect', required=True)
p.add_argument('--log', type=Path, required=True)
p.add_argument('--allow-camera-for', help='Test app display name whose expected camera dialog may be accepted')
p.add_argument('--timeout', type=int, default=60)
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
# FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK clears this test application's
# old task, including an external picker abandoned by an earlier failed run.
print(call('shell', 'am', 'start', '-W', '-f', '0x10008000', '-n', activity))
deadline = time.monotonic() + a.timeout
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
        resumed = '\n'.join(line for line in call('shell', 'dumpsys', 'activity', 'activities').splitlines()
            if 'topResumedActivity=' in line or 'mResumedActivity:' in line)
        if a.app_id + '/' in resumed:
            print('\n'.join(line[line.index('PASS:'):] for line in output.splitlines() if 'PASS:' in line))
            print('PASS: application returned to foreground; no external picker left open')
            break
    if a.allow_camera_for and 'WAIT: requesting camera permission;' in output:
        call('shell', 'uiautomator', 'dump', '/sdcard/dotnative-verification-window.xml')
        window = ET.fromstring(call('shell', 'cat', '/sdcard/dotnative-verification-window.xml'))
        message = next((node.get('text', '') for node in window.iter('node')
            if node.get('resource-id') == 'com.android.permissioncontroller:id/permission_message'), '')
        if a.allow_camera_for in message and 'take pictures and record video' in message:
            for node in window.iter('node'):
                if node.get('resource-id') == 'com.android.permissioncontroller:id/permission_allow_foreground_only_button':
                    x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds', '')))
                    call('shell', 'input', 'tap', str((x1 + x2) // 2), str((y1 + y2) // 2))
    time.sleep(1)
else:
    raise SystemExit('Timed out waiting for native check; see ' + str(a.log))
