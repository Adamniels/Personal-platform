from fastapi.testclient import TestClient

from brain.app import create_app
from brain.settings import load


def test_health_answers_ok() -> None:
    client = TestClient(create_app(load({})))

    # No token, no cookie: the health endpoint is public.
    response = client.get("/brain/api/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_only_the_brains_own_prefix_is_served() -> None:
    # FastAPI's documentation pages and schema route sit outside /brain/api/
    # and are switched off, so every route is the brain's own.
    client = TestClient(create_app(load({})))

    for path in ["/docs", "/redoc", "/openapi.json", "/brain/api/openapi.json"]:
        assert client.get(path).status_code == 404, path
