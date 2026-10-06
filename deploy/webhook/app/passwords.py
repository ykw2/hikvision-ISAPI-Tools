"""管理頁密碼。初始密碼只印一次，磁碟上只留 scrypt 雜湊。"""

from __future__ import annotations

import base64
import hashlib
import hmac
import os
import secrets
import string
from pathlib import Path

SYMBOLS = "!@#$%^&*-_=+"
MIN_NEW_PASSWORD = 6
_N = 2**14
_R = 8
_P = 1
_DKLEN = 32


def generate_password(length: int = 24) -> str:
    """產生固定含大寫、小寫、數字與符號的密碼。"""
    if length < 4:
        raise ValueError("密碼長度至少要 4，才放得下每一種字元")
    required = [
        secrets.choice(string.ascii_uppercase),
        secrets.choice(string.ascii_lowercase),
        secrets.choice(string.digits),
        secrets.choice(SYMBOLS),
    ]
    pool = string.ascii_uppercase + string.ascii_lowercase + string.digits + SYMBOLS
    chars = required + [secrets.choice(pool) for _ in range(length - 4)]
    secrets.SystemRandom().shuffle(chars)
    return "".join(chars)


def acceptable_new_password(password: str) -> bool:
    return len(password.strip()) >= MIN_NEW_PASSWORD


def hash_password(password: str) -> str:
    salt = secrets.token_bytes(16)
    digest = hashlib.scrypt(password.encode("utf-8"), salt=salt, n=_N, r=_R, p=_P, dklen=_DKLEN)
    salt_b64 = base64.b64encode(salt).decode("ascii")
    hash_b64 = base64.b64encode(digest).decode("ascii")
    return f"scrypt${_N}${_R}${_P}${salt_b64}${hash_b64}"


def verify_password(stored: str, password: str) -> bool:
    try:
        kind, n_raw, r_raw, p_raw, salt_b64, hash_b64 = stored.strip().split("$")
        if kind != "scrypt":
            return False
        salt = base64.b64decode(salt_b64)
        expected = base64.b64decode(hash_b64)
        digest = hashlib.scrypt(
            password.encode("utf-8"),
            salt=salt,
            n=int(n_raw),
            r=int(r_raw),
            p=int(p_raw),
            dklen=len(expected),
        )
    except (ValueError, TypeError):
        return False
    return hmac.compare_digest(digest, expected)


def write_password_hash(path: Path, password: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = hash_password(password)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(payload, encoding="utf-8")
    os.chmod(temporary, 0o600)
    os.replace(temporary, path)


def read_password_hash(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def ensure_gui_password(path: Path, *, print_initial: bool = False) -> str | None:
    """密碼檔已存在就沿用。沒有才產生，並在要求時把明文印一次。"""
    if path.exists() and path.stat().st_size > 0:
        return None
    password = generate_password()
    write_password_hash(path, password)
    if print_initial:
        print(f"管理頁初始密碼：{password}", flush=True)
    return password


def ensure_session_secret(path: Path) -> bytes:
    if path.exists() and path.stat().st_size >= 32:
        return path.read_bytes()
    secret = secrets.token_bytes(32)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(secret)
    os.chmod(temporary, 0o600)
    os.replace(temporary, path)
    return secret


def main() -> None:
    from app.settings import Settings

    settings = Settings.from_env()
    settings.data_dir.mkdir(parents=True, exist_ok=True)
    ensure_gui_password(settings.password_path, print_initial=True)


if __name__ == "__main__":
    main()
