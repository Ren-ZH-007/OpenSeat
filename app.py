import copy
import os
import queue
import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, simpledialog
from pathlib import Path
from uuid import uuid4
import core
import devices

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title('OpenSeat · 多座位配置开发版 0.1')
        self.geometry('1180x740')
        self.minsize(900, 580)
        self.path = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'OpenSeat' / 'config.json'
        self.config_data = core.defaults()
        error = None
        if self.path.exists():
            try:
                self.config_data = core.load(self.path)
            except Exception as exc:
                error = str(exc)
        self.saved = copy.deepcopy(self.config_data)
        self.inventory = []
        self.demo_mode = False
        self.scanning = False
        self.events = queue.Queue()
        style = ttk.Style(self)
        if 'clam' in style.theme_names():
            style.theme_use('clam')
        style.configure('.', font=('Microsoft YaHei UI', 10))
        style.configure('Treeview', rowheight=32)
        style.configure('Title.TLabel', font=('Microsoft YaHei UI', 22, 'bold'))
        wrapper = ttk.Frame(self, padding=24)
        wrapper.pack(fill='both', expand=True)
        ttk.Label(wrapper, text='OpenSeat  多座位工作台', style='Title.TLabel').pack(anchor='w')
        ttk.Label(wrapper, text='本地配置 · 无激活授权 · 无应用账号与角色', foreground='#52657b').pack(anchor='w', pady=(6, 16))
        ttk.Label(wrapper, text='开发版：仅管理设备与工位配置。独立桌面、键鼠隔离、音频路由尚未实现。', foreground='#a54700').pack(anchor='w')
        bar = ttk.Frame(wrapper)
        bar.pack(fill='x', pady=16)
        for title, action in [('扫描真实设备', self.scan), ('演示设备', self.show_demo), ('检查配置', self.check), ('保存配置', self.save), ('撤销修改', self.revert), ('导入', self.import_config), ('导出', self.export_config)]:
            ttk.Button(bar, text=title, command=action).pack(side='left', padx=(0, 6))
        pane = ttk.Panedwindow(wrapper, orient='horizontal')
        pane.pack(fill='both', expand=True)
        left, right = ttk.Frame(pane, padding=8), ttk.Frame(pane, padding=8)
        pane.add(left, weight=1)
        pane.add(right, weight=4)
        ttk.Label(left, text='工位规划（最多 12 个）').pack(anchor='w', pady=6)
        self.seats = tk.Listbox(left, exportselection=False, font=('Microsoft YaHei UI', 11), width=22, relief='flat', selectbackground='#2563eb')
        self.seats.pack(fill='both', expand=True)
        for title, action in [('新增工位', self.add_seat), ('重命名', self.rename_seat), ('删除选中工位', self.delete_seat)]:
            ttk.Button(left, text=title, command=action).pack(fill='x', pady=4)
        ttk.Label(right, text='设备清单 · 按 Ctrl 可多选 · 分配仅修改配置').pack(anchor='w', pady=6)
        self.tree = ttk.Treeview(right, columns=('kind', 'name', 'seat', 'status'), show='headings', selectmode='extended')
        for key, title, width in [('kind', '类型', 90), ('name', '设备名称', 270), ('seat', '配置归属', 120), ('status', '状态', 90)]:
            self.tree.heading(key, text=title)
            self.tree.column(key, width=width)
        scroll = ttk.Scrollbar(right, orient='vertical', command=self.tree.yview)
        self.tree.configure(yscrollcommand=scroll.set)
        scroll.pack(side='right', fill='y')
        self.tree.pack(fill='both', expand=True)
        actions = ttk.Frame(right)
        actions.pack(fill='x', pady=8)
        ttk.Button(actions, text='分配到左侧选中工位', command=self.assign).pack(side='left', padx=4)
        ttk.Button(actions, text='取消设备分配', command=self.unassign).pack(side='left', padx=4)
        ttk.Button(actions, text='查看设备 ID', command=self.details).pack(side='left', padx=4)
        self.status = tk.StringVar(value='就绪。请扫描真实设备或加载演示设备。')
        ttk.Label(wrapper, textvariable=self.status, wraplength=1050).pack(anchor='w', pady=(12, 0))
        self.protocol('WM_DELETE_WINDOW', self.close)
        self.refresh()
        self.after(100, self.poll)
        if error:
            self.after(200, lambda: messagebox.showerror('配置读取失败，原文件未改动', error))

    def refresh(self):
        selection = self.seats.curselection()
        index = selection[0] if selection else 0
        self.seats.delete(0, 'end')
        for seat in self.config_data['seats']:
            self.seats.insert('end', seat['name'])
        self.seats.selection_set(min(index, len(self.config_data['seats']) - 1))
        self.tree.delete(*self.tree.get_children())
        names = {s['id']: s['name'] for s in self.config_data['seats']}
        online = {d['id'] for d in self.inventory}
        rows = self.inventory + [{'id': d, 'kind': '', 'name': d, 'status': '离线 / 未扫描'} for d in self.config_data['assignments'] if d not in online]
        self.row_ids = {}
        for n, d in enumerate(rows):
            item = str(n)
            self.row_ids[item] = d['id']
            self.tree.insert('', 'end', iid=item, values=(core.KINDS.get(d['kind'], d['kind']), d['name'], names.get(self.config_data['assignments'].get(d['id']), '未分配'), d['status']))
        dirty = self.config_data != self.saved
        self.title('OpenSeat · 多座位配置开发版 0.1' + (' * 未保存' if dirty else ''))

    def selected_seat(self):
        selected = self.seats.curselection()
        return self.config_data['seats'][selected[0]] if selected else None

    def add_seat(self):
        if len(self.config_data['seats']) >= 12:
            return messagebox.showinfo('工位上限', '本开发版允许规划最多 12 个工位。')
        self.config_data['seats'].append({'id': str(uuid4()), 'name': '新工位'})
        self.refresh()

    def rename_seat(self):
        seat = self.selected_seat()
        if seat:
            name = simpledialog.askstring('重命名', '输入 1–40 个字符', initialvalue=seat['name'])
            if name and 0 < len(name.strip()) <= 40:
                seat['name'] = name.strip()
                self.refresh()

    def delete_seat(self):
        seat = self.selected_seat()
        if not seat or len(self.config_data['seats']) == 1:
            return
        if messagebox.askyesno('删除工位', '删除此工位并解除其设备配置？'):
            self.config_data['seats'].remove(seat)
            self.config_data['assignments'] = {d: s for d, s in self.config_data['assignments'].items() if s != seat['id']}
            self.refresh()

    def assign(self):
        seat = self.selected_seat()
        if seat:
            for row in self.tree.selection():
                self.config_data['assignments'][self.row_ids[row]] = seat['id']
            self.refresh()

    def unassign(self):
        for row in self.tree.selection():
            self.config_data['assignments'].pop(self.row_ids[row], None)
        self.refresh()

    def details(self):
        ids = [self.row_ids[row] for row in self.tree.selection()]
        if ids:
            messagebox.showinfo('设备实例 ID', '\n\n'.join(ids))

    def scan(self):
        if self.scanning:
            return
        self.scanning = True
        self.status.set('正在扫描 Windows PnP 设备…')
        def worker():
            try:
                self.events.put(('ok', devices.scan()))
            except Exception as exc:
                self.events.put(('error', str(exc)))
        threading.Thread(target=worker, daemon=True).start()

    def poll(self):
        try:
            kind, payload = self.events.get_nowait()
            self.scanning = False
            if kind == 'ok':
                self.inventory, self.demo_mode = payload, False
                self.refresh()
                self.status.set(f'扫描完成：{len(payload)} 个 PnP 设备节点。复合设备可能有多个节点；未建立 USB 拓扑。')
            else:
                self.status.set('扫描失败，保留此前设备清单。')
                messagebox.showerror('扫描失败', payload)
        except queue.Empty:
            pass
        self.after(100, self.poll)

    def show_demo(self):
        if self.scanning:
            return
        self.inventory, self.demo_mode = devices.demo(), True
        self.refresh()
        self.status.set('演示模式：列表为模拟设备，不代表本机设备。DEMO 前缀会保留在导出配置中。')

    def check(self):
        messagebox.showinfo('配置检查' + (' · 演示数据' if self.demo_mode else ''), '\n\n'.join(core.inspect(self.config_data, self.inventory)))

    def save(self):
        try:
            core.save(self.path, self.config_data)
            self.saved = copy.deepcopy(self.config_data)
            self.refresh()
            self.status.set('已保存配置；未应用任何设备隔离。位置：' + str(self.path))
            return True
        except Exception as exc:
            messagebox.showerror('保存失败', str(exc))
            return False

    def can_discard(self):
        if self.config_data == self.saved:
            return True
        answer = messagebox.askyesnocancel('未保存修改', '是否保存当前配置？选择“否”丢弃修改。')
        return self.save() if answer is True else answer is False

    def revert(self):
        if messagebox.askyesno('撤销', '恢复上次保存的配置？'):
            self.config_data = copy.deepcopy(self.saved)
            self.refresh()

    def import_config(self):
        if not self.can_discard():
            return
        path = filedialog.askopenfilename(filetypes=[('JSON 配置', '*.json')])
        if path:
            try:
                candidate = core.load(path)
                self.config_data = candidate
                self.refresh()
                self.status.set('已导入为待保存配置。')
            except Exception as exc:
                messagebox.showerror('导入失败，当前配置未更改', str(exc))

    def export_config(self):
        path = filedialog.asksaveasfilename(defaultextension='.json', filetypes=[('JSON 配置', '*.json')])
        if path:
            try:
                core.save(path, self.config_data)
                self.status.set('配置已导出：' + path)
            except Exception as exc:
                messagebox.showerror('导出失败', str(exc))

    def close(self):
        if self.can_discard():
            self.destroy()

if __name__ == '__main__':
    App().mainloop()
