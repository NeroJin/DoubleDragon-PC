import ctypes, sys, time
from ctypes import wintypes
u = ctypes.windll.user32

def find(title_sub):
    res = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        n = u.GetWindowTextLengthW(h)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            if title_sub.lower() in b.value.lower():
                res.append((h, b.value))
        return True
    u.EnumWindows(cb, 0)
    return res

VK = {'enter':0x0D,'space':0x20,'up':0x26,'down':0x28,'left':0x25,'right':0x27,
      'esc':0x1B,'tab':0x09,'bs':0x08,'y':0x59,'a':0x41,'b':0x42,'x':0x58,
      'lb':0x51,'rb':0x45,'start':0x0D,'back':0x08}

def press(key, hold=0.06):
    vk = VK.get(key.lower())
    if vk is None and len(key) == 1:
        vk = ord(key.upper())
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(hold)
    u.keybd_event(vk, 0, 2, 0)
    time.sleep(0.12)

if __name__ == '__main__':
    ws = find('DoubleDragon')
    if not ws:
        print('window not found'); sys.exit(1)
    hwnd, title = ws[0]
    print('found:', hex(hwnd), title)
    u.ShowWindow(hwnd, 9)
    u.SetForegroundWindow(hwnd)
    time.sleep(0.5)
    for k in sys.argv[1:]:
        press(k)
        print('pressed', k)
    time.sleep(0.3)

def click_at(hwnd, fx=0.5, fy=0.5):
    r = wintypes.RECT()
    u.GetClientRect(hwnd, ctypes.byref(r))
    pt = wintypes.POINT(0, 0)
    u.ClientToScreen(hwnd, ctypes.byref(pt))
    x = pt.x + int((r.right - r.left) * fx)
    y = pt.y + int((r.bottom - r.top) * fy)
    u.SetCursorPos(x, y)
    time.sleep(0.15)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.08)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.35)
