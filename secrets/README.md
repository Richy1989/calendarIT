# secrets/

Local-only files for signing release builds of the Android app. **Everything in this folder except
this README and `keystore.properties.example` is ignored by git** (see the root `.gitignore`) and
by Docker (`.dockerignore`). Never force-add anything here (`git add -f`).

## Setup

1. Copy your keystore here, e.g. `secrets/planit-release.jks`.
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

`PLANIT_KEYSTORE_FILE` (absolute path), `PLANIT_KEYSTORE_PASSWORD`, `PLANIT_KEY_ALIAS`,
`PLANIT_KEY_PASSWORD` take precedence over the file.

## No keystore yet?

```bash
keytool -genkeypair -v -keystore secrets/planit-release.jks -alias planit \
  -keyalg RSA -keysize 4096 -validity 10000
```

Back the keystore and its passwords up somewhere safe outside this repo: an app signed with a lost
key can never be updated again (on Play, enrol in Play App Signing so Google holds the app key).
