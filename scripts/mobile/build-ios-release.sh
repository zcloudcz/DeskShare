#!/usr/bin/env bash
# Signed App Store build of the Flutter app (mobile/) for CI, manual signing with an Apple Distribution
# certificate and an App Store provisioning profile for cz.zcloud.deskshare (both from CI secrets).
# Same approach as the other zcloud iOS apps; the temporary keychain and profile are removed on exit.
set -Eeuo pipefail
umask 077

readonly REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly APP_DIR="${REPO_ROOT}/mobile"
readonly IOS_DIR="${APP_DIR}/ios"
readonly BUNDLE_ID="cz.zcloud.deskshare"

die() { echo "build-ios-release: $*" >&2; exit 1; }

for variable in IOS_CERTIFICATE_BASE64 IOS_CERTIFICATE_PASSWORD IOS_PROVISION_PROFILE_BASE64 IOS_TEAM_ID BUILD_NUMBER; do
  [[ -n "${!variable:-}" ]] || die "${variable} is required"
done
[[ "${IOS_TEAM_ID}" =~ ^[A-Za-z0-9]{10}$ ]] || die "IOS_TEAM_ID must be 10 alphanumeric characters"

TEMP_ROOT="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/deskshare-ios.XXXXXX")"
KEYCHAIN_PASSWORD="$(openssl rand -hex 24)"
KEYCHAIN_PATH="${TEMP_ROOT}/signing.keychain-db"
CERTIFICATE_PATH="${TEMP_ROOT}/certificate.p12"
PROFILE_PATH="${TEMP_ROOT}/profile.mobileprovision"
PROFILE_PLIST="${TEMP_ROOT}/profile.plist"
INSTALLED_PROFILE=""
PBXPROJ_PATH="${IOS_DIR}/Runner.xcodeproj/project.pbxproj"
PBXPROJ_BACKUP="${TEMP_ROOT}/project.pbxproj"

cleanup() {
  set +e
  [[ -n "${INSTALLED_PROFILE}" ]] && rm -f -- "${INSTALLED_PROFILE}"
  security delete-keychain "${KEYCHAIN_PATH}" >/dev/null 2>&1
  [[ -f "${PBXPROJ_BACKUP}" ]] && cp -- "${PBXPROJ_BACKUP}" "${PBXPROJ_PATH}"
  rm -rf -- "${TEMP_ROOT}"
}
trap cleanup EXIT

printf '%s' "${IOS_CERTIFICATE_BASE64}" | base64 -D >"${CERTIFICATE_PATH}"
printf '%s' "${IOS_PROVISION_PROFILE_BASE64}" | base64 -D >"${PROFILE_PATH}"

security create-keychain -p "${KEYCHAIN_PASSWORD}" "${KEYCHAIN_PATH}"
security set-keychain-settings -lut 21600 "${KEYCHAIN_PATH}"
security unlock-keychain -p "${KEYCHAIN_PASSWORD}" "${KEYCHAIN_PATH}"
security import "${CERTIFICATE_PATH}" -P "${IOS_CERTIFICATE_PASSWORD}" -A -t cert -f pkcs12 -k "${KEYCHAIN_PATH}" >/dev/null
security set-key-partition-list -S apple-tool:,apple: -k "${KEYCHAIN_PASSWORD}" "${KEYCHAIN_PATH}" >/dev/null
security list-keychain -d user -s "${KEYCHAIN_PATH}"

security cms -D -i "${PROFILE_PATH}" -o "${PROFILE_PLIST}"
PROFILE_UUID="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "${PROFILE_PLIST}")"
PROFILE_TEAM="$(/usr/libexec/PlistBuddy -c 'Print :TeamIdentifier:0' "${PROFILE_PLIST}")"
PROFILE_APP_ID="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' "${PROFILE_PLIST}")"
[[ "${PROFILE_TEAM}" == "${IOS_TEAM_ID}" ]] || die "provisioning profile team ${PROFILE_TEAM} is not ${IOS_TEAM_ID}"
[[ "${PROFILE_APP_ID}" == "${IOS_TEAM_ID}.${BUNDLE_ID}" ]] || die "provisioning profile is for ${PROFILE_APP_ID}, not ${IOS_TEAM_ID}.${BUNDLE_ID}"

PROFILE_DIR="${HOME}/Library/MobileDevice/Provisioning Profiles"
mkdir -p -- "${PROFILE_DIR}"
INSTALLED_PROFILE="${PROFILE_DIR}/${PROFILE_UUID}.mobileprovision"
cp -- "${PROFILE_PATH}" "${INSTALLED_PROFILE}"

SIGNING_IDENTITY="$(security find-identity -v -p codesigning "${KEYCHAIN_PATH}" | awk '/^[[:space:]]*[0-9]+\)/ { print $2; exit }')"
[[ "${SIGNING_IDENTITY}" =~ ^[A-Fa-f0-9]{40}$ ]] || die "no code-signing identity in the imported certificate"

cd -- "${APP_DIR}"
flutter pub get
# Generates the Xcode config and the Podfile, and runs pod install.
flutter build ios --release --no-codesign --config-only --build-number="${BUILD_NUMBER}"

cp -- "${PBXPROJ_PATH}" "${PBXPROJ_BACKUP}"
ruby -r xcodeproj -e '
  path, team, identity, profile = ARGV
  project = Xcodeproj::Project.open(File.dirname(path))
  target = project.targets.find { |t| t.name == "Runner" } or abort "Runner target not found"
  target.build_configurations.each do |configuration|
    next unless configuration.name == "Release"
    settings = configuration.build_settings
    settings["CODE_SIGN_STYLE"] = "Manual"
    settings["DEVELOPMENT_TEAM"] = team
    settings["CODE_SIGN_IDENTITY"] = identity
    settings["PROVISIONING_PROFILE_SPECIFIER"] = profile
  end
  project.save
' "${PBXPROJ_PATH}" "${IOS_TEAM_ID}" "${SIGNING_IDENTITY}" "${PROFILE_UUID}"

ARCHIVE_PATH="${APP_DIR}/build/ios/archive/Runner.xcarchive"
EXPORT_DIR="${APP_DIR}/build/ios/ipa"
EXPORT_OPTIONS="${TEMP_ROOT}/ExportOptions.plist"
mkdir -p -- "${EXPORT_DIR}"

xcodebuild archive -workspace "${IOS_DIR}/Runner.xcworkspace" -scheme Runner -configuration Release \
  -archivePath "${ARCHIVE_PATH}" -destination 'generic/platform=iOS'

cat >"${EXPORT_OPTIONS}" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>method</key><string>app-store-connect</string>
<key>signingStyle</key><string>manual</string>
<key>teamID</key><string>${IOS_TEAM_ID}</string>
<key>provisioningProfiles</key><dict><key>${BUNDLE_ID}</key><string>${PROFILE_UUID}</string></dict>
</dict></plist>
PLIST

xcodebuild -exportArchive -archivePath "${ARCHIVE_PATH}" -exportPath "${EXPORT_DIR}" -exportOptionsPlist "${EXPORT_OPTIONS}"
compgen -G "${EXPORT_DIR}/*.ipa" >/dev/null || die "signed IPA was not produced"
