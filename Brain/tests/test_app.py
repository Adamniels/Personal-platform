import uuid

from fastapi.testclient import TestClient

from brain import db
from brain.app import create_app
from brain.settings import load
from tests.harness import app_url
from tests.test_settings import DATABASE


def test_health_answers_ok() -> None:
    client = TestClient(create_app(load(DATABASE)))

    # No token, no cookie: the health endpoint is public.
    response = client.get("/brain/api/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_only_the_brains_own_prefix_is_served() -> None:
    # FastAPI's documentation pages and schema route sit outside /brain/api/
    # and are switched off, so every route is the brain's own.
    client = TestClient(create_app(load(DATABASE)))

    for path in ["/docs", "/redoc", "/openapi.json", "/brain/api/openapi.json"]:
        assert client.get(path).status_code == 404, path


def test_the_pool_opens_with_the_app_and_closes_with_it(database: str) -> None:
    app = create_app(load({"BRAIN_APP_DATABASE_URL": app_url(database)}))

    async def who(pool: db.Pool) -> object:
        async with db.transaction(pool, uuid.uuid4()) as conn:
            cursor = await conn.execute("SELECT current_user")
            return await cursor.fetchone()

    with TestClient(app) as client:
        pool = app.state.pool
        assert not pool.closed
        # The running brain connects as the app role, never the owner.
        assert client.portal is not None
        assert client.portal.call(who, pool) == ("brain_app",)
    assert pool.closed
