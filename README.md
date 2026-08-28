# HowToFish.RoomInfo v1.3

用途：解决 Wine/盖世游戏里《How to Fish》原生“复制房号”无法同步到 Android 输入法剪贴板的问题。

行为：
- 游戏进入暂停状态时，在右上角显示“房间信息”面板。
- 点击“刷新房号”时，只扫描一次运行中的 MonoBehaviour，寻找安全的房号复制方法（CopyRoomCode / CopyLobbyCode / Copy...Code）。
- 找到后调用游戏原生复制方法，再读取 Wine 的 CF_UNICODETEXT / Unity systemCopyBuffer。
- 即使自动调用没找到，也可以先点游戏原生“复制房号”，面板会在暂停期间自动从 Wine 剪贴板读取。
- 不依赖 Android 剪贴板桥接。
