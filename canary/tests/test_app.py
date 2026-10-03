from starlette.testclient import TestClient

from canary.app import app

client = TestClient(app)


def test_echoes_the_path_it_received():
    response = client.get("/canary/anything")

    assert response.status_code == 200
    assert "/canary/anything" in response.text


def test_escapes_the_path_it_echoes():
    response = client.get("/canary/%3Cscript%3E")

    assert "<script>" not in response.text
    assert "&lt;script&gt;" in response.text
