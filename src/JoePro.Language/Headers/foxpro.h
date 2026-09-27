* Joe Pro built-in foxpro.h: the constants FoxPro programs most often take from the header of that name.
* It is used when a program says #INCLUDE foxpro.h and no file of that name is found next to it or along
* SET PATH; a foxpro.h of your own always wins. Constants that are not listed here compile as variable names.

* MESSAGEBOX() buttons, icons and defaults
#DEFINE MB_OK                   0
#DEFINE MB_OKCANCEL             1
#DEFINE MB_ABORTRETRYIGNORE     2
#DEFINE MB_YESNOCANCEL          3
#DEFINE MB_YESNO                4
#DEFINE MB_RETRYCANCEL          5
#DEFINE MB_ICONSTOP             16
#DEFINE MB_ICONQUESTION         32
#DEFINE MB_ICONEXCLAMATION      48
#DEFINE MB_ICONINFORMATION      64
#DEFINE MB_APPLMODAL            0
#DEFINE MB_DEFBUTTON1           0
#DEFINE MB_DEFBUTTON2           256
#DEFINE MB_DEFBUTTON3           512
#DEFINE MB_SYSTEMMODAL          4096

* MESSAGEBOX() return values
#DEFINE IDOK                    1
#DEFINE IDCANCEL                2
#DEFINE IDABORT                 3
#DEFINE IDRETRY                 4
#DEFINE IDIGNORE                5
#DEFINE IDYES                   6
#DEFINE IDNO                    7

* Colors (RGB values)
#DEFINE COLOR_BLACK             0
#DEFINE COLOR_DARK_RED          128
#DEFINE COLOR_RED               255
#DEFINE COLOR_DARK_GREEN        32768
#DEFINE COLOR_DARK_YELLOW       32896
#DEFINE COLOR_GREEN             65280
#DEFINE COLOR_YELLOW            65535
#DEFINE COLOR_DARK_BLUE         8388608
#DEFINE COLOR_DARK_MAGENTA      8388736
#DEFINE COLOR_DARK_CYAN         8421376
#DEFINE COLOR_DARK_GRAY         8421504
#DEFINE COLOR_GRAY              12632256
#DEFINE COLOR_BLUE              16711680
#DEFINE COLOR_MAGENTA           16711935
#DEFINE COLOR_CYAN              16776960
#DEFINE COLOR_WHITE             16777215

* MousePointer
#DEFINE MOUSE_DEFAULT           0
#DEFINE MOUSE_ARROW             1
#DEFINE MOUSE_CROSSHAIR         2
#DEFINE MOUSE_IBEAM             3
#DEFINE MOUSE_ICON_POINTER      4
#DEFINE MOUSE_SIZE_POINTER      5
#DEFINE MOUSE_SIZE_NE_SW        6
#DEFINE MOUSE_SIZE_VERTICAL     7
#DEFINE MOUSE_SIZE_NW_SE        8
#DEFINE MOUSE_SIZE_HORIZONTAL   9
#DEFINE MOUSE_UP_ARROW          10
#DEFINE MOUSE_HOURGLASS         11
#DEFINE MOUSE_NO_DROP           12
#DEFINE MOUSE_HIDE_POINTER      13
#DEFINE MOUSE_ARROW2            14
#DEFINE MOUSE_HAND              15
#DEFINE MOUSE_CUSTOM            99

* Mouse buttons and shift keys (MouseDown, MouseUp, MouseMove)
#DEFINE BUTTON_LEFT             1
#DEFINE BUTTON_RIGHT            2
#DEFINE BUTTON_MIDDLE           4
#DEFINE SHIFT_MASK              1
#DEFINE CTRL_MASK               2
#DEFINE ALT_MASK                4

* Drag and drop
#DEFINE DRAG_MANUAL             0
#DEFINE DRAG_AUTOMATIC          1
#DEFINE DRAG_CANCEL             0
#DEFINE DRAG_BEGIN              1
#DEFINE DRAG_END                2
#DEFINE DRAG_ENTER              0
#DEFINE DRAG_LEAVE              1
#DEFINE DRAG_OVER               2

* WindowState, WindowType, BorderStyle
#DEFINE WINDOWSTATE_NORMAL      0
#DEFINE WINDOWSTATE_MINIMIZED   1
#DEFINE WINDOWSTATE_MAXIMIZED   2
#DEFINE WINDOWTYPE_MODELESS     0
#DEFINE WINDOWTYPE_MODAL        1
#DEFINE BORDER_NONE             0
#DEFINE BORDER_SINGLE           1
#DEFINE BORDER_DOUBLE           2
#DEFINE BORDER_SYSTEM           3

* Alignment, ScrollBars
#DEFINE ALIGN_LEFT              0
#DEFINE ALIGN_RIGHT             1
#DEFINE ALIGN_CENTER            2
#DEFINE SCROLLBAR_NONE          0
#DEFINE SCROLLBAR_HORZ          1
#DEFINE SCROLLBAR_VERT          2
#DEFINE SCROLLBAR_BOTH          3

* Table buffering (CURSORSETPROP("Buffering"))
#DEFINE DB_BUFOFF               1
#DEFINE DB_BUFLOCKRECORD        2
#DEFINE DB_BUFOPTRECORD         3
#DEFINE DB_BUFLOCKTABLE         4
#DEFINE DB_BUFOPTTABLE          5

* Update types of remote views
#DEFINE DB_UPDATE               1
#DEFINE DB_DELETEINSERT         2
#DEFINE DB_KEY                  1
#DEFINE DB_KEYANDUPDATABLE      2
#DEFINE DB_KEYANDMODIFIED       3
#DEFINE DB_KEYANDTIMESTAMP      4

* Transactions of remote connections
#DEFINE DB_TRANSAUTO            1
#DEFINE DB_TRANSMANUAL          2

* Timer
#DEFINE TIMER_DISABLED          0
