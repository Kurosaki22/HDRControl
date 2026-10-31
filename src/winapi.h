#pragma once

#include <Windows.h>

#include <cstdint>
#include <format>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

[[noreturn]] inline void ThrowWinErr(std::string_view message, LONG errorCode) {
  throw std::runtime_error(
      std::format("{} (Windows-Fehlercode {})", message, errorCode));
}

inline std::vector<DISPLAYCONFIG_PATH_INFO> QueryDisplayConfigImpl() {
  std::vector<DISPLAYCONFIG_PATH_INFO> paths;
  std::vector<DISPLAYCONFIG_MODE_INFO> modes;
  constexpr UINT32 flags = QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE;

  while (true) {
    UINT32 pathCount = 0;
    UINT32 modeCount = 0;
    LONG result = ::GetDisplayConfigBufferSizes(flags, &pathCount, &modeCount);
    if (result != ERROR_SUCCESS) {
      ThrowWinErr("GetDisplayConfigBufferSizes ist fehlgeschlagen", result);
    }

    paths.resize(pathCount);
    modes.resize(modeCount);
    result = ::QueryDisplayConfig(flags, &pathCount, paths.data(), &modeCount,
                                  modes.data(), nullptr);
    paths.resize(pathCount);
    modes.resize(modeCount);

    if (result == ERROR_SUCCESS) {
      return paths;
    }
    if (result != ERROR_INSUFFICIENT_BUFFER) {
      ThrowWinErr("QueryDisplayConfig ist fehlgeschlagen", result);
    }
  }
}

inline DISPLAYCONFIG_TARGET_DEVICE_NAME GetDisplayName(LUID adapterId,
                                                        UINT32 targetId) {
  DISPLAYCONFIG_TARGET_DEVICE_NAME targetName{};
  targetName.header.adapterId = adapterId;
  targetName.header.id = targetId;
  targetName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
  targetName.header.size = sizeof(targetName);

  const LONG result = ::DisplayConfigGetDeviceInfo(&targetName.header);
  if (result != ERROR_SUCCESS) {
    ThrowWinErr("DisplayConfigGetDeviceInfo (Monitorname) ist fehlgeschlagen",
                result);
  }
  return targetName;
}

inline DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO GetAdvancedColorInfo(
    LUID adapterId, UINT32 targetId) {
  DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO colorInfo{};
  colorInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
  colorInfo.header.size = sizeof(colorInfo);
  colorInfo.header.adapterId = adapterId;
  colorInfo.header.id = targetId;

  const LONG result = ::DisplayConfigGetDeviceInfo(&colorInfo.header);
  if (result != ERROR_SUCCESS) {
    ThrowWinErr("HDR-Status konnte nicht gelesen werden", result);
  }
  return colorInfo;
}

inline DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 GetAdvancedColorInfo2(
    LUID adapterId, UINT32 targetId) {
  DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 colorInfo{};
  colorInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
  colorInfo.header.size = sizeof(colorInfo);
  colorInfo.header.adapterId = adapterId;
  colorInfo.header.id = targetId;

  const LONG result = ::DisplayConfigGetDeviceInfo(&colorInfo.header);
  if (result != ERROR_SUCCESS) {
    ThrowWinErr("HDR-Status (erweiterte Abfrage) konnte nicht gelesen werden",
                result);
  }
  return colorInfo;
}

inline void SetAdvancedColorInfo(LUID adapterId, UINT32 targetId,
                                 bool enabled) {
  DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE colorState{};
  colorState.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
  colorState.header.size = sizeof(colorState);
  colorState.header.adapterId = adapterId;
  colorState.header.id = targetId;
  colorState.enableAdvancedColor = enabled ? TRUE : FALSE;

  const LONG result = ::DisplayConfigSetDeviceInfo(&colorState.header);
  if (result != ERROR_SUCCESS) {
    ThrowWinErr("HDR-Einstellung konnte nicht geändert werden", result);
  }
}
