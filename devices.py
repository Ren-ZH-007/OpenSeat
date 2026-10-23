import json
import os
import subprocess

SCRIPT = r'''
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$items = @(Get-PnpDevice -PresentOnly | Where-Object { $_.Class -in @('Monitor','Keyboard','Mouse','AudioEndpoint','USB','HIDClass') } | ForEach-Object {
    [PSCustomObject]@{id=$_.InstanceId; name=$_.FriendlyName; kind=$_.Class; status=$_.Status}
})
ConvertTo-Json -InputObject $items -Compress
'''

def scan():
    if os.name != 'nt':
        raise RuntimeError('真实设备扫描仅支持 Windows；可使用演示模式查看界面。')
    result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', SCRIPT], capture_output=True, timeout=40, creationflags=0x08000000)
    if result.returncode:
        raise RuntimeError(result.stderr.decode('utf-8', errors='replace')[:1200] or '设备扫描失败')
    rows = json.loads(result.stdout.decode('utf-8-sig') or '[]')
    if not isinstance(rows, list):
        raise RuntimeError('Windows 返回了无法识别的设备列表')
    return [{'id': r['id'], 'name': r.get('name') or r['id'], 'kind': r['kind'], 'status': r.get('status', '')} for r in rows]

def demo():
    return [{'id': f'DEMO/{n}/{kind}', 'name': f'演示 {name} {n}', 'kind': kind, 'status': '演示数据'} for n in (1, 2) for kind, name in [('Monitor', '显示器'), ('Keyboard', '键盘'), ('Mouse', '鼠标'), ('AudioEndpoint', '音频')]]
