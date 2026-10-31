#!/usr/bin/env bash
# Upload the signed IPA from build-ios-release.sh to App Store Connect (TestFlight) with an API key.
set -euo pipefail
for name in APP_STORE_CONNECT_KEY_ID APP_STORE_CONNECT_ISSUER_ID APP_STORE_CONNECT_PRIVATE_KEY_BASE64; do
  [[ -n "${!name:-}" ]] || { echo "Missing required secret: $name" >&2; exit 1; }
done
[[ "$APP_STORE_CONNECT_KEY_ID" =~ ^[A-Z0-9]+$ ]] || { echo 'Invalid App Store Connect key ID' >&2; exit 1; }
[[ "$APP_STORE_CONNECT_ISSUER_ID" =~ ^[0-9a-fA-F-]{36}$ ]] || { echo 'Invalid App Store Connect issuer ID' >&2; exit 1; }
key_dir=$(mktemp -d "${RUNNER_TEMP:?}/deskshare-appstore.XXXXXX")
trap 'rm -rf "$key_dir"' EXIT
umask 077
printf '%s' "$APP_STORE_CONNECT_PRIVATE_KEY_BASE64" | base64 --decode > "$key_dir/AuthKey_$APP_STORE_CONNECT_KEY_ID.p8"
export API_PRIVATE_KEYS_DIR="$key_dir"
packages=(mobile/build/ios/ipa/*.ipa)
[[ ${#packages[@]} == 1 && -f "${packages[0]}" ]] || { echo 'Expected exactly one signed IPA' >&2; exit 1; }
xcrun altool --upload-app --type ios --file "${packages[0]}" --apiKey "$APP_STORE_CONNECT_KEY_ID" --apiIssuer "$APP_STORE_CONNECT_ISSUER_ID"
