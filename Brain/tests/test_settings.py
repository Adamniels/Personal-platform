import os
import subprocess
import sys
from pathlib import Path

import pytest

from brain.settings import Settings, SettingsError, load


def test_settings_default_and_refuse_a_malformed_value() -> None:
    assert load({}) == Settings(
        core_url="http://127.0.0.1:8080",
        public_origin="https://platform.test",
        web_dir=Path("web/dist"),
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
        ("BRAIN_WEB_DIR", ""),
    ]:
        with pytest.raises(SettingsError, match=name):
            load({name: value})

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
        assert load({"BRAIN_CORE_URL": core_url}).core_url == core_url

    for origin in [
        "https://platform.test",
        "https://platform.test:8443",
        "http://localhost:8080",
    ]:
        assert load({"BRAIN_PUBLIC_ORIGIN": origin}).public_origin == origin

    # A folder is taken as it is: whether it holds a build is checked when the
    # brain starts, see test_web.py.
    for web_dir in ["web/dist", "/srv/brain/web", "relative"]:
        assert load({"BRAIN_WEB_DIR": web_dir}).web_dir == Path(web_dir)
