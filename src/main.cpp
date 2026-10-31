#include "display.h"

#include <CommCtrl.h>
#include <Windows.h>

#include <algorithm>
#include <array>
#include <exception>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace {

constexpr wchar_t kWindowClass[] = L"HDRControlWindow";
constexpr int kListId = 100;
constexpr int kEnableId = 101;
constexpr int kDisableId = 102;
constexpr int kRefreshId = 103;
constexpr int kStatusId = 104;

HMENU ControlId(int id) {
  return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

struct AppState {
  HWND window = nullptr;
  HWND list = nullptr;
  HWND enableButton = nullptr;
  HWND disableButton = nullptr;
  HWND refreshButton = nullptr;
  HWND status = nullptr;
  std::vector<Display> displays;
};

std::wstring ToWide(const char* utf8) {
  if (utf8 == nullptr || *utf8 == '\0') {
    return L"Unbekannter Fehler";
  }

  const int inputLength = static_cast<int>(std::char_traits<char>::length(utf8));
  const int outputLength =
      MultiByteToWideChar(CP_UTF8, 0, utf8, inputLength, nullptr, 0);
  if (outputLength <= 0) {
    return L"Fehlertext konnte nicht dargestellt werden";
  }

  std::wstring result(static_cast<size_t>(outputLength), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, utf8, inputLength, result.data(),
                      outputLength);
  return result;
}

std::wstring StatusText(HdrStatus status) {
  switch (status) {
    case HdrStatus::NotSupported:
      return L"Nicht unterstützt";
    case HdrStatus::Enabled:
      return L"HDR aktiv";
    case HdrStatus::Disabled:
      return L"HDR aus";
    case HdrStatus::Unknown:
      return L"Unbekannt";
  }
  return L"Unbekannt";
}

void SetStatusText(AppState& state, const std::wstring& text) {
  if (state.status != nullptr) {
    SetWindowTextW(state.status, text.c_str());
  }
}

void UpdateActionButtons(AppState& state) {
  int selectedCount = 0;
  bool allSelectedSupportHdr = true;
  int index = -1;
  while ((index = ListView_GetNextItem(state.list, index, LVNI_SELECTED)) !=
         -1) {
    ++selectedCount;
    if (static_cast<size_t>(index) >= state.displays.size() ||
        !state.displays[static_cast<size_t>(index)].isHdrSupported()) {
      allSelectedSupportHdr = false;
    }
  }

  const BOOL canChange = selectedCount > 0 && allSelectedSupportHdr;
  EnableWindow(state.enableButton, canChange);
  EnableWindow(state.disableButton, canChange);
}

void ShowError(HWND owner, const std::wstring& message) {
  MessageBoxW(owner, message.c_str(), L"HDR Control – Fehler",
              MB_OK | MB_ICONERROR);
}

std::optional<std::wstring> RefreshDisplays(AppState& state) {
  try {
    auto displays = Display::QueryAllDisplays();
    state.displays = std::move(displays);
    ListView_DeleteAllItems(state.list);

    for (size_t i = 0; i < state.displays.size(); ++i) {
      const auto& display = state.displays[i];
      LVITEMW item{};
      item.mask = LVIF_TEXT;
      item.iItem = static_cast<int>(i);
      item.pszText = const_cast<wchar_t*>(display.getName().c_str());
      const int row = ListView_InsertItem(state.list, &item);
      if (row < 0) {
        throw std::runtime_error("Monitor konnte nicht in der Liste "
                                 "dargestellt werden");
      }

      const auto status = StatusText(display.getHdrStatus());
      ListView_SetItemText(state.list, row, 1,
                           const_cast<wchar_t*>(status.c_str()));

      const auto targetId = std::to_wstring(display.getTargetId());
      ListView_SetItemText(state.list, row, 2,
                           const_cast<wchar_t*>(targetId.c_str()));
    }

    UpdateActionButtons(state);
    if (state.displays.empty()) {
      SetStatusText(state, L"Keine aktiven Monitore gefunden.");
    } else {
      SetStatusText(
          state, L"Monitor(e) auswählen; Mehrfachauswahl mit Strg/Umschalt.");
    }
    return std::nullopt;
  } catch (const std::exception& error) {
    state.displays.clear();
    ListView_DeleteAllItems(state.list);
    UpdateActionButtons(state);
    SetStatusText(state, L"Monitore konnten nicht geladen werden.");
    return ToWide(error.what());
  }
}

void ChangeHdrForSelection(AppState& state, bool enable) {
  std::wstring errors;
  int index = -1;
  while ((index = ListView_GetNextItem(state.list, index, LVNI_SELECTED)) !=
         -1) {
    const auto displayIndex = static_cast<size_t>(index);
    if (displayIndex >= state.displays.size()) {
      errors += L"Die Monitorauswahl ist veraltet. Bitte aktualisieren.\n";
      continue;
    }

    auto& display = state.displays[displayIndex];
    try {
      display.setHdrEnabled(enable);
    } catch (const std::exception& error) {
      errors += display.getName() + L": " + ToWide(error.what()) + L"\n";
    }
  }

  if (const auto refreshError = RefreshDisplays(state)) {
    errors += L"Die Statusanzeige konnte nicht aktualisiert werden: " +
              *refreshError + L"\n";
  }

  if (errors.empty()) {
    SetStatusText(state, enable ? L"HDR wurde für die Auswahl eingeschaltet."
                                : L"HDR wurde für die Auswahl ausgeschaltet.");
  } else {
    SetStatusText(state, L"Die Aktion wurde nicht für alle Monitore ausgeführt.");
    ShowError(state.window, errors);
  }
}

bool CreateControls(AppState& state) {
  state.list = CreateWindowExW(
      WS_EX_CLIENTEDGE, WC_LISTVIEWW, L"",
      WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SHOWSELALWAYS,
      0, 0, 0, 0, state.window, ControlId(kListId),
      GetModuleHandleW(nullptr), nullptr);
  state.enableButton = CreateWindowExW(
      0, L"BUTTON", L"HDR einschalten", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
      0, 0, 0, 0, state.window, ControlId(kEnableId),
      GetModuleHandleW(nullptr), nullptr);
  state.disableButton = CreateWindowExW(
      0, L"BUTTON", L"HDR ausschalten", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
      0, 0, 0, 0, state.window, ControlId(kDisableId),
      GetModuleHandleW(nullptr), nullptr);
  state.refreshButton = CreateWindowExW(
      0, L"BUTTON", L"Aktualisieren", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
      0, 0, 0, 0, state.window, ControlId(kRefreshId),
      GetModuleHandleW(nullptr), nullptr);
  state.status = CreateWindowExW(
      0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_LEFT,
      0, 0, 0, 0, state.window, ControlId(kStatusId),
      GetModuleHandleW(nullptr), nullptr);

  if (state.list == nullptr || state.enableButton == nullptr ||
      state.disableButton == nullptr || state.refreshButton == nullptr ||
      state.status == nullptr) {
    return false;
  }

  ListView_SetExtendedListViewStyle(state.list,
                                    LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);

  LVCOLUMNW column{};
  column.mask = LVCF_TEXT | LVCF_WIDTH;
  column.pszText = const_cast<wchar_t*>(L"Monitor");
  column.cx = 360;
  if (ListView_InsertColumn(state.list, 0, &column) < 0) {
    return false;
  }
  column.pszText = const_cast<wchar_t*>(L"HDR-Status");
  column.cx = 150;
  if (ListView_InsertColumn(state.list, 1, &column) < 0) {
    return false;
  }
  column.pszText = const_cast<wchar_t*>(L"Ziel-ID");
  column.cx = 100;
  if (ListView_InsertColumn(state.list, 2, &column) < 0) {
    return false;
  }

  const std::array<HWND, 5> childControls{
      state.list, state.enableButton, state.disableButton, state.refreshButton,
      state.status};
  const auto font = reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT));
  for (const HWND control : childControls) {
    SendMessageW(control, WM_SETFONT, font, TRUE);
  }

  EnableWindow(state.enableButton, FALSE);
  EnableWindow(state.disableButton, FALSE);
  return true;
}

void LayoutControls(AppState& state, int width, int height) {
  constexpr int margin = 12;
  constexpr int buttonHeight = 30;
  constexpr int buttonGap = 8;
  constexpr int statusHeight = 24;
  constexpr int refreshWidth = 112;
  constexpr int actionWidth = 148;

  const int statusY = height - margin - buttonHeight - statusHeight;
  const int listHeight = std::max(0, statusY - margin * 2);
  MoveWindow(state.list, margin, margin, std::max(0, width - margin * 2),
             listHeight, TRUE);
  MoveWindow(state.status, margin, statusY,
             std::max(0, width - margin * 2), statusHeight, TRUE);
  MoveWindow(state.enableButton, margin, height - margin - buttonHeight,
             actionWidth, buttonHeight, TRUE);
  MoveWindow(state.disableButton, margin + actionWidth + buttonGap,
             height - margin - buttonHeight, actionWidth, buttonHeight, TRUE);
  MoveWindow(state.refreshButton, width - margin - refreshWidth,
             height - margin - buttonHeight, refreshWidth, buttonHeight, TRUE);

  const int availableWidth = std::max(0, width - margin * 2);
  ListView_SetColumnWidth(state.list, 0, std::max(180, availableWidth - 250));
  ListView_SetColumnWidth(state.list, 1, 150);
  ListView_SetColumnWidth(state.list, 2, 100);
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam,
                            LPARAM lParam) {
  auto* state = reinterpret_cast<AppState*>(
      GetWindowLongPtrW(window, GWLP_USERDATA));

  if (message == WM_NCCREATE) {
    const auto* createInfo =
        reinterpret_cast<const CREATESTRUCTW*>(lParam);
    state = static_cast<AppState*>(createInfo->lpCreateParams);
    state->window = window;
    SetWindowLongPtrW(window, GWLP_USERDATA,
                      reinterpret_cast<LONG_PTR>(state));
  }

  switch (message) {
    case WM_CREATE:
      if (!CreateControls(*state)) {
        ShowError(window, L"Die Fenster-Steuerelemente konnten nicht erstellt "
                          L"werden.");
        return -1;
      }
      if (const auto error = RefreshDisplays(*state)) {
        ShowError(window, *error);
      }
      return 0;

    case WM_SIZE:
      if (state != nullptr && state->list != nullptr) {
        LayoutControls(*state, LOWORD(lParam), HIWORD(lParam));
      }
      return 0;

    case WM_COMMAND:
      if (state == nullptr) {
        break;
      }
      switch (LOWORD(wParam)) {
        case kEnableId:
          ChangeHdrForSelection(*state, true);
          return 0;
        case kDisableId:
          ChangeHdrForSelection(*state, false);
          return 0;
        case kRefreshId:
          if (const auto error = RefreshDisplays(*state)) {
            ShowError(window, *error);
          }
          return 0;
      }
      break;

    case WM_NOTIFY:
      if (state != nullptr && state->list != nullptr) {
        const auto* header = reinterpret_cast<const NMHDR*>(lParam);
        if (header->idFrom == kListId && header->code == LVN_ITEMCHANGED) {
          UpdateActionButtons(*state);
        }
      }
      return 0;

    case WM_DESTROY:
      PostQuitMessage(0);
      return 0;

    case WM_NCCREATE:
      break;
  }

  return DefWindowProcW(window, message, wParam, lParam);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int showCommand) {
  INITCOMMONCONTROLSEX controls{};
  controls.dwSize = sizeof(controls);
  controls.dwICC = ICC_LISTVIEW_CLASSES;
  if (!InitCommonControlsEx(&controls)) {
    MessageBoxW(nullptr, L"Die Windows-Steuerelemente konnten nicht geladen "
                         L"werden.",
                L"HDR Control – Fehler", MB_OK | MB_ICONERROR);
    return 1;
  }

  WNDCLASSEXW windowClass{};
  windowClass.cbSize = sizeof(windowClass);
  windowClass.hInstance = instance;
  windowClass.lpfnWndProc = WindowProc;
  windowClass.lpszClassName = kWindowClass;
  windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
  windowClass.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
  windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
  if (RegisterClassExW(&windowClass) == 0) {
    MessageBoxW(nullptr, L"Das Hauptfenster konnte nicht registriert werden.",
                L"HDRControl – Fehler", MB_OK | MB_ICONERROR);
    return 1;
  }

  AppState state;
  const HWND window = CreateWindowExW(
      0, kWindowClass, L"HDR Control", WS_OVERLAPPEDWINDOW,
      CW_USEDEFAULT, CW_USEDEFAULT, 760, 480, nullptr, nullptr, instance,
      &state);
  if (window == nullptr) {
    MessageBoxW(nullptr, L"Das Hauptfenster konnte nicht erstellt werden.",
                L"HDR Control – Fehler", MB_OK | MB_ICONERROR);
    return 1;
  }

  ShowWindow(window, showCommand);
  UpdateWindow(window);

  MSG message{};
  while (true) {
    const BOOL result = GetMessageW(&message, nullptr, 0, 0);
    if (result == 0) {
      return static_cast<int>(message.wParam);
    }
    if (result == -1) {
      MessageBoxW(window, L"Die Windows-Nachrichtenschleife ist fehlgeschlagen.",
                  L"HDR Control – Fehler", MB_OK | MB_ICONERROR);
      return 1;
    }
    TranslateMessage(&message);
    DispatchMessageW(&message);
  }
}
