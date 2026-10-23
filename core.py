"""OpenSeat configuration model. No OS isolation is performed."""
import json
import os
import tempfile
from pathlib import Path

KINDS = {'Monitor': '显示器', 'Keyboard': '键盘', 'Mouse': '鼠标', 'AudioEndpoint': '音频', 'USB': 'USB', 'HIDClass': 'HID'}

def defaults():
    return {'version': 1, 'seats': [{'id': 'seat-1', 'name': '主工位'}, {'id': 'seat-2', 'name': '工位 2'}], 'assignments': {}}

def validate(data):
    if not isinstance(data, dict) or data.get('version') != 1:
        raise ValueError('不支持的配置格式')
    seats = data.get('seats')
    if not isinstance(seats, list) or not 1 <= len(seats) <= 12:
        raise ValueError('工位数量必须为 1–12（配置范围，不代表运行能力）')
    ids = []
    for seat in seats:
        if not isinstance(seat, dict) or not isinstance(seat.get('id'), str) or not seat['id']:
            raise ValueError('工位 ID 无效')
        if not isinstance(seat.get('name'), str) or not seat['name'].strip() or len(seat['name']) > 40:
            raise ValueError('工位名称应为 1–40 个字符')
        ids.append(seat['id'])
    if len(set(ids)) != len(ids):
        raise ValueError('工位 ID 重复')
    assignments = data.get('assignments')
    if not isinstance(assignments, dict):
        raise ValueError('设备分配格式无效')
    for device, seat in assignments.items():
        if not isinstance(device, str) or not device or not isinstance(seat, str) or seat not in ids:
            raise ValueError('设备分配引用了不存在的工位')
    return data

def save(path, data):
    validate(data)
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=path.parent, suffix='.tmp')
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)

def load(path):
    return validate(json.loads(Path(path).read_text(encoding='utf-8')))

def inspect(data, devices):
    validate(data)
    online = {d['id']: d for d in devices}
    issues = []
    for device in data['assignments']:
        if device not in online:
            issues.append('已分配设备离线：' + device)
    for seat in data['seats']:
        kinds = {online[d]['kind'] for d, s in data['assignments'].items() if s == seat['id'] and d in online}
        missing = [KINDS[k] for k in ('Monitor', 'Keyboard', 'Mouse') if k not in kinds]
        if missing:
            issues.append(seat['name'] + ' 缺少：' + '、'.join(missing))
    issues.append('隔离引擎未实现：当前分配仅保存配置，不影响 Windows 设备归属。')
    return issues
