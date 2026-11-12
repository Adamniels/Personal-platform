import os
import subprocess
import sys

import pytest

from brain.settings import Settings, SettingsError, load

# What the database setting needs to parse; no test here connects with it.
DATABASE = {
    "BRAIN_APP_DATABASE_URL": "postgres://brain_app:secret-a1b2@127.0.0.1:5432/brain"
}


def test_settings_default_and_refuse_a_malformed_value() -> None:
    assert load(DATABASE) == Settings(
        core_url="http://127.0.0.1:8080",
        public_origin="https://platform.test",
        app_database_url="postgres://brain_app:secret-a1b2@127.0.0.1:5432/brain",
    )

    for name, value in [
        ("BRAIN_CORE_URL", ""),
        ("BRAIN_CORE_URL", "127.0.0.1:8080"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:8080/"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:8080/core"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:8080?x"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:8080#x"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:http"),
        ("BRAIN_CORE_URL", "http://127.0.0.1:99999"),
        ("BRAIN_CORE_URL", "http://user@127.0.0.1:8080"),
        ("BRAIN_CORE_URL", "ftp://127.0.0.1:8080"),
        ("BRAIN_CORE_URL", "http://"),
        ("BRAIN_CORE_URL", " http://127.0.0.1:8080"),
        ("BRAIN_PUBLIC_ORIGIN", ""),
        ("BRAIN_PUBLIC_ORIGIN", "platform.test"),
        ("BRAIN_PUBLIC_ORIGIN", "https://"),
        ("BRAIN_PUBLIC_ORIGIN", "https://platform.test/"),
        ("BRAIN_PUBLIC_ORIGIN", "https://platform.test/path"),
        ("BRAIN_PUBLIC_ORIGIN", "https://platform.test?x"),
        ("BRAIN_PUBLIC_ORIGIN", "https://Platform.test"),
        ("BRAIN_PUBLIC_ORIGIN", "HTTPS://platform.test"),
        ("BRAIN_PUBLIC_ORIGIN", "https://platform.test:443"),
        ("BRAIN_PUBLIC_ORIGIN", "http://platform.test:80"),
        ("BRAIN_PUBLIC_ORIGIN", "https://platform.test:"),
        ("BRAIN_PUBLIC_ORIGIN", "https://user@platform.test"),
        ("BRAIN_PUBLIC_ORIGIN", "ftp://platform.test"),
        ("BRAIN_PUBLIC_ORIGIN", " https://platform.test"),
    ]:
        with pytest.raises(SettingsError, match=name):
            load({**DATABASE, name: value})

    # A malformed value stops the service at import, before uvicorn serves.
    environ = {**os.environ, "BRAIN_CORE_URL": "http://127.0.0.1:8080/"}
    started = subprocess.run(
        [sys.executable, "-c", "import brain.app"],
        check=False,
        env=environ,
        capture_output=True,
        text=True,
    )

    assert started.returncode != 0
    assert "SettingsError: BRAIN_CORE_URL" in started.stderr


def test_a_well_formed_value_is_taken_as_it_is() -> None:
    for core_url in [
        "http://127.0.0.1:8080",
        "http://localhost:9000",
        "https://core.internal",
        "http://[::1]:8080",
    ]:
        assert load({**DATABASE, "BRAIN_CORE_URL": core_url}).core_url == core_url

    for origin in [
        "https://platform.test",
        "https://platform.test:8443",
        "http://localhost:8080",
    ]:
        assert load({**DATABASE, "BRAIN_PUBLIC_ORIGIN": origin}).public_origin == origin


def test_the_database_setting_is_required_and_never_shown() -> None:
    # No default: there is no connection string that is right everywhere,
    # and one with a password does not belong in the code.
    with pytest.raises(SettingsError, match="BRAIN_APP_DATABASE_URL is not set"):
        load({})

    for value in [
        "",
        "brain_app:secret-a1b2@127.0.0.1:5432/brain",
        "http://brain_app:secret-a1b2@127.0.0.1:5432/brain",
        "postgres://brain_app:secret-a1b2@/brain",
        "postgres://brain_app:secret-a1b2@127.0.0.1:5432",
        "postgres://brain_app:secret-a1b2@127.0.0.1:5432/",
        "postgres://brain_app:secret-a1b2@127.0.0.1:port/brain",
    ]:
        with pytest.raises(SettingsError, match="BRAIN_APP_DATABASE_URL") as refused:
            load({"BRAIN_APP_DATABASE_URL": value})
        # The value holds the password, so the error never repeats it.
        assert "secret-a1b2" not in str(refused.value)

    for value in [
        "postgres://brain_app:secret-a1b2@127.0.0.1:5432/brain",
        "postgresql://brain_app@localhost/brain_test_1?sslmode=disable",
    ]:
        settings = load({"BRAIN_APP_DATABASE_URL": value})
        assert settings.app_database_url == value
        assert "secret-a1b2" not in repr(settings)
