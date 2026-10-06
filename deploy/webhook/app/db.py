"""事件佇列就是 SQLite 裡的一列，網頁與轉送看同一份資料。"""

from __future__ import annotations

import sqlite3
import threading
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).isoformat(timespec="seconds")


class Store:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._conn = sqlite3.connect(path, timeout=5, check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA foreign_keys=ON")
        self._conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                received_at TEXT NOT NULL,
                source_ip TEXT NOT NULL,
                content_type TEXT NOT NULL,
                event_type TEXT NOT NULL,
                channel TEXT NOT NULL,
                event_time TEXT NOT NULL,
                event_xml TEXT NOT NULL,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT,
                last_error TEXT,
                sent_at TEXT
            );
            CREATE TABLE IF NOT EXISTS images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL REFERENCES events(id) ON DELETE CASCADE,
                filename TEXT NOT NULL,
                content_type TEXT NOT NULL,
                size INTEGER NOT NULL,
                data BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_events_queue
                ON events(status, next_attempt_at, received_at);
            CREATE INDEX IF NOT EXISTS idx_events_received ON events(received_at);
            """
        )
        self._conn.commit()

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    def recover_sending(self) -> None:
        with self._lock:
            self._conn.execute("UPDATE events SET status = 'pending' WHERE status = 'sending'")
            self._conn.commit()

    def add_event(
        self,
        *,
        event_id: str,
        received_at: str,
        source_ip: str,
        content_type: str,
        event_type: str,
        channel: str,
        event_time: str,
        event_xml: str,
        images: list[dict[str, Any]],
    ) -> bool:
        """寫入成功回 True。主鍵重複代表攝影機重送，回 False。"""
        with self._lock:
            try:
                self._conn.execute(
                    """
                    INSERT INTO events (
                        id, received_at, source_ip, content_type, event_type, channel,
                        event_time, event_xml, status, attempts
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'pending', 0)
                    """,
                    (
                        event_id,
                        received_at,
                        source_ip,
                        content_type,
                        event_type,
                        channel,
                        event_time,
                        event_xml,
                    ),
                )
                for image in images:
                    self._conn.execute(
                        """
                        INSERT INTO images (event_id, filename, content_type, size, data)
                        VALUES (?, ?, ?, ?, ?)
                        """,
                        (
                            event_id,
                            image["filename"],
                            image["content_type"],
                            image["size"],
                            image["data"],
                        ),
                    )
                self._conn.commit()
            except sqlite3.IntegrityError:
                self._conn.rollback()
                return False
            return True

    def list_events(self, status: str | None, limit: int = 200) -> list[dict[str, Any]]:
        sql = "SELECT * FROM events"
        params: list[Any] = []
        if status == "pending":
            sql += " WHERE status IN ('pending', 'sending')"
        elif status in {"sent", "failed"}:
            sql += " WHERE status = ?"
            params.append(status)
        sql += " ORDER BY received_at DESC LIMIT ?"
        params.append(limit)
        with self._lock:
            rows = self._conn.execute(sql, params).fetchall()
        return [dict(row) for row in rows]

    def count_events(self) -> int:
        with self._lock:
            row = self._conn.execute("SELECT COUNT(*) AS n FROM events").fetchone()
        return int(row["n"])

    def get_event(self, event_id: str) -> dict[str, Any] | None:
        with self._lock:
            row = self._conn.execute("SELECT * FROM events WHERE id = ?", (event_id,)).fetchone()
        return dict(row) if row else None

    def list_images(self, event_id: str, *, include_data: bool = False) -> list[dict[str, Any]]:
        columns = "id, event_id, filename, content_type, size"
        if include_data:
            columns += ", data"
        with self._lock:
            rows = self._conn.execute(
                f"SELECT {columns} FROM images WHERE event_id = ? ORDER BY id",
                (event_id,),
            ).fetchall()
        return [dict(row) for row in rows]

    def get_image(self, event_id: str, image_id: int) -> dict[str, Any] | None:
        with self._lock:
            row = self._conn.execute(
                "SELECT * FROM images WHERE event_id = ? AND id = ?",
                (event_id, image_id),
            ).fetchone()
        return dict(row) if row else None

    def claim_pending(self, now: datetime) -> dict[str, Any] | None:
        stamp = iso(now)
        with self._lock:
            row = self._conn.execute(
                """
                SELECT * FROM events
                WHERE status = 'pending'
                  AND (next_attempt_at IS NULL OR next_attempt_at <= ?)
                ORDER BY received_at
                LIMIT 1
                """,
                (stamp,),
            ).fetchone()
            if row is None:
                return None
            self._conn.execute("UPDATE events SET status = 'sending' WHERE id = ?", (row["id"],))
            self._conn.commit()
            claimed = dict(row)
        claimed["status"] = "sending"
        return claimed

    def mark_sent(self, event_id: str, now: datetime) -> None:
        with self._lock:
            self._conn.execute(
                """
                UPDATE events
                SET status = 'sent', sent_at = ?, last_error = NULL, next_attempt_at = NULL
                WHERE id = ?
                """,
                (iso(now), event_id),
            )
            self._conn.commit()

    def mark_failure(self, event_id: str, error: str, now: datetime, max_attempts: int) -> str:
        with self._lock:
            row = self._conn.execute("SELECT attempts FROM events WHERE id = ?", (event_id,)).fetchone()
            if row is None:
                return "missing"
            attempts = int(row["attempts"]) + 1
            if attempts >= max_attempts:
                self._conn.execute(
                    """
                    UPDATE events
                    SET status = 'failed', attempts = ?, last_error = ?, next_attempt_at = NULL
                    WHERE id = ?
                    """,
                    (attempts, error[:500], event_id),
                )
                status = "failed"
            else:
                delay = min(300, 2**attempts)
                next_at = iso(now + timedelta(seconds=delay))
                self._conn.execute(
                    """
                    UPDATE events
                    SET status = 'pending', attempts = ?, last_error = ?, next_attempt_at = ?
                    WHERE id = ?
                    """,
                    (attempts, error[:500], next_at, event_id),
                )
                status = "pending"
            self._conn.commit()
        return status

    def retry_event(self, event_id: str) -> bool:
        with self._lock:
            cursor = self._conn.execute(
                """
                UPDATE events
                SET status = 'pending', attempts = 0, next_attempt_at = NULL, last_error = NULL
                WHERE id = ? AND status IN ('pending', 'failed')
                """,
                (event_id,),
            )
            self._conn.commit()
            return cursor.rowcount > 0

    def delete_event(self, event_id: str) -> bool:
        with self._lock:
            cursor = self._conn.execute("DELETE FROM events WHERE id = ?", (event_id,))
            self._conn.commit()
            return cursor.rowcount > 0
