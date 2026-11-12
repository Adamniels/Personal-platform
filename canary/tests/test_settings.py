import os
import subprocess
import sys

import pytest

from canary.settings import Settings, SettingsError, load


def test_settings_default_and_refuse_a_malformed_value():
    assert load({}) == Settings(
        core_url="http://127.0.0.1:8080", public_origin="https://platform.test"
    )

    for name, value in [
        ("CANARY_CORE_URL", ""),
        ("CANARY_CORE_URL", "127.0.0.1:8080"),
        ("CANARY_CORE_URL", "http://127.0.0.1:8080/"),
        ("CANARY_CORE_URL", "http://127.0.0.1:8080/core"),
        ("CANARY_CORE_URL", "http://127.0.0.1:8080?x"),
        ("CANARY_CORE_URL", "http://127.0.0.1:8080#x"),
        ("CANARY_CORE_URL", "http://127.0.0.1:"),
        ("CANARY_CORE_URL", "http://127.0.0.1:http"),
        ("CANARY_CORE_URL", "http://127.0.0.1:99999"),
        ("CANARY_CORE_URL", "http://user@127.0.0.1:8080"),
        ("CANARY_CORE_URL", "ftp://127.0.0.1:8080"),
        ("CANARY_CORE_URL", "http://"),
        ("CANARY_CORE_URL", " http://127.0.0.1:8080"),
        ("CANARY_PUBLIC_ORIGIN", ""),
        ("CANARY_PUBLIC_ORIGIN", "platform.test"),
        ("CANARY_PUBLIC_ORIGIN", "https://"),
        ("CANARY_PUBLIC_ORIGIN", "https://platform.test/"),
        ("CANARY_PUBLIC_ORIGIN", "https://platform.test/path"),
        ("CANARY_PUBLIC_ORIGIN", "https://platform.test?x"),
        ("CANARY_PUBLIC_ORIGIN", "https://Platform.test"),
        ("CANARY_PUBLIC_ORIGIN", "HTTPS://platform.test"),
        ("CANARY_PUBLIC_ORIGIN", "https://platform.test:443"),
        ("CANARY_PUBLIC_ORIGIN", "http://platform.test:80"),
        ("CANARY_PUBLIC_ORIGIN", "https://platform.test:"),
        ("CANARY_PUBLIC_ORIGIN", "https://user@platform.test"),
        ("CANARY_PUBLIC_ORIGIN", "ftp://platform.test"),
        ("CANARY_PUBLIC_ORIGIN", " https://platform.test"),
    ]:
        with pytest.raises(SettingsError, match=name):
            load({name: value})


def test_a_well_formed_value_is_taken_as_it_is():
    for core_url in [
        "http://127.0.0.1:8080",
        "http://localhost:9000",
        "https://core.internal",
        "http://[::1]:8080",
    ]:
        assert load({"CANARY_CORE_URL": core_url}).core_url == core_url

    for origin in [
        "https://platform.test",
        "https://platform.test:8443",
        "http://localhost:8080",
    ]:
        assert load({"CANARY_PUBLIC_ORIGIN": origin}).public_origin == origin


def test_canary_refuses_to_start_with_a_malformed_value():
    environ = {**os.environ, "CANARY_CORE_URL": "http://127.0.0.1:8080/"}
    started = subprocess.run(
        [sys.executable, "-c", "import canary.app"],
        check=False,
        env=environ,
        capture_output=True,
        text=True,
    )

    assert started.returncode != 0
    assert "SettingsError: CANARY_CORE_URL" in started.stderr
