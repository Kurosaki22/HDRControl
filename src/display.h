#pragma once

#include "winapi.h"

#include <optional>
#include <string>
#include <vector>

enum class HdrStatus {
  NotSupported,
  Enabled,
  Disabled,
  Unknown,
};

class Display {
 public:
  explicit Display(const DISPLAYCONFIG_PATH_INFO& displayInfo)
      : adapterId_(displayInfo.sourceInfo.adapterId),
        targetId_(displayInfo.targetInfo.id) {
    const auto targetName = GetDisplayName(adapterId_, targetId_);
    name_ = targetName.flags.friendlyNameFromEdid
                ? targetName.monitorFriendlyDeviceName
                : L"Unbekannter Monitor";
    updateHdrStatus();
  }

  const std::wstring& getName() const { return name_; }
  UINT32 getTargetId() const { return targetId_; }
  HdrStatus getHdrStatus() const { return hdrStatus_; }
  bool isHdrEnabled() const { return hdrStatus_ == HdrStatus::Enabled; }
  bool isHdrSupported() const {
    return hdrStatus_ == HdrStatus::Enabled ||
           hdrStatus_ == HdrStatus::Disabled;
  }

  void setHdrEnabled(bool enabled) {
    if (!isHdrSupported()) {
      throw std::runtime_error("HDR wird von diesem Monitor nicht unterstützt "
                               "oder der Status ist unbekannt");
    }
    SetAdvancedColorInfo(adapterId_, targetId_, enabled);
    updateHdrStatus();
  }

  static std::vector<Display> QueryAllDisplays() {
    std::vector<Display> displays;
    for (const auto& path : QueryDisplayConfigImpl()) {
      displays.emplace_back(path);
    }
    return displays;
  }

 private:
  LUID adapterId_{};
  UINT32 targetId_{};
  HdrStatus hdrStatus_ = HdrStatus::Unknown;
  std::wstring name_;

  void updateHdrStatus() {
    static bool useAdvancedColorInfo2 = true;
    if (useAdvancedColorInfo2) {
      if (const auto status = tryGetAdvancedColorInfo2()) {
        hdrStatus_ = *status;
        return;
      }
      useAdvancedColorInfo2 = false;
    }

    const auto colorInfo = GetAdvancedColorInfo(adapterId_, targetId_);
    if (!colorInfo.advancedColorSupported) {
      hdrStatus_ = HdrStatus::NotSupported;
    } else if (colorInfo.advancedColorEnabled) {
      hdrStatus_ = HdrStatus::Enabled;
    } else {
      hdrStatus_ = HdrStatus::Disabled;
    }
  }

  std::optional<HdrStatus> tryGetAdvancedColorInfo2() const {
    try {
      const auto colorInfo = GetAdvancedColorInfo2(adapterId_, targetId_);
      if (!colorInfo.highDynamicRangeSupported) {
        return HdrStatus::NotSupported;
      }

      switch (colorInfo.activeColorMode) {
        case DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR:
        case DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG:
          return HdrStatus::Disabled;
        case DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR:
          return HdrStatus::Enabled;
        default:
          return HdrStatus::Unknown;
      }
    } catch (const std::exception&) {
      return std::nullopt;
    }
  }
};
