# secrets/

Local-only files for signing release builds of the Android app. **Everything in this folder except
this README and `keystore.properties.example` is ignored by git** (see the root `.gitignore`) and
by Docker (`.dockerignore`). Never force-add anything here (`git add -f`).

## Setup

1. Copy your keystore here, e.g. `secrets/24seven-release.jks`.
2. `cp secrets/keystore.properties.example secrets/keystore.properties`
3. Fill in `secrets/keystore.properties` (file name of the keystore, alias, both passwords).
4. Restrict access: `chmod 700 secrets && chmod 600 secrets/*.jks secrets/keystore.properties`

Then, from `android/`:

```bash
./gradlew assembleFossRelease    # app/build/outputs/apk/foss/release/app-foss-release.apk
./gradlew bundlePlayRelease      # app/build/outputs/bundle/playRelease/app-play-release.aab (Play Store)
```

Without `keystore.properties` (or the environment variables below) release builds still work but
come out **unsigned**.

Check that nothing secret is tracked: `git status --ignored secrets/` lists them as ignored, and
`git check-ignore -v secrets/keystore.properties` names the rule.

## Environment variables instead (CI)

`RELEASE_KEYSTORE_FILE` (absolute path), `RELEASE_KEYSTORE_PASSWORD`, `RELEASE_KEY_ALIAS`,
`RELEASE_KEY_PASSWORD` take precedence over the file.

The GitHub release pipeline (`.github/workflows/release.yml`, on a `vX.Y.Z` tag)
needs four repository secrets (Settings → Secrets and variables → Actions):

| Secret | Value |
| --- | --- |
| `RELEASE_KEYSTORE_BASE64` | `base64 -w0 secrets/24seven-release.p12` |
| `RELEASE_KEYSTORE_PASSWORD` | `storePassword` from `keystore.properties` |
| `RELEASE_KEY_ALIAS` | `keyAlias` |
| `RELEASE_KEY_PASSWORD` | `keyPassword` |

With `gh`: `base64 -w0 secrets/24seven-release.p12 | gh secret set RELEASE_KEYSTORE_BASE64`, and
`gh secret set RELEASE_KEYSTORE_PASSWORD` (prompts) for the others.

## No keystore yet?

```bash
keytool -genkeypair -v -storetype PKCS12 -keystore secrets/24seven-release.p12 -alias 24seven \
  -keyalg RSA -keysize 4096 -validity 10000
```

Back the keystore and its passwords up somewhere safe outside this repo: an app signed with a lost
key can never be updated again (on Play, enrol in Play App Signing so Google holds the app key).
