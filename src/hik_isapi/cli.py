"""hik-isapi 命令列。"""

from __future__ import annotations

import argparse
import csv
import os
import sys
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path

from hik_isapi.client import IsapiClient
from hik_isapi.errors import ConfigError, TransportError
from hik_isapi.inventory import (
    Camera,
    inventory_warnings,
    load_failed_ids,
    load_inventory,
    select_cameras,
)
from hik_isapi.profile import Profile, Step, load_profile
from hik_isapi.runner import CameraResult, RunReport, Runner, resolve_cameras
from hik_isapi.version import __version__
from hik_isapi.xmlutil import extract_device_info


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return int(args.func(args))
    except ConfigError as exc:
        print(f"設定錯誤：{exc}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("已中斷", file=sys.stderr)
        return 130


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="hik-isapi",
        description="用 ISAPI 對大量海康威視攝影機套用同一套設定",
        epilog="建議先 validate，再 probe，接著 apply --dry-run --limit 5，最後才全量套用。",
    )
    parser.add_argument("--version", action="version", version=f"hik-isapi {__version__}")
    commands = parser.add_subparsers(dest="command", required=True)

    validate = commands.add_parser("validate", help="檢查清單與設定檔，不連線")
    _add_target_args(validate, profile_required=True)
    _add_filter_args(validate)
    validate.set_defaults(func=cmd_validate)

    probe = commands.add_parser("probe", help="只讀取每支攝影機的裝置資訊")
    _add_target_args(probe, profile_required=False)
    _add_filter_args(probe)
    _add_run_args(probe)
    probe.set_defaults(func=cmd_probe)

    apply = commands.add_parser("apply", help="把設定檔套用到清單中的攝影機")
    _add_target_args(apply, profile_required=True)
    _add_filter_args(apply)
    _add_run_args(apply)
    apply.add_argument("--dry-run", action="store_true", help="仍會 GET 讀取現況，但不送出 PUT、POST、DELETE")
    apply.add_argument("--retry-failed", metavar="REPORT.json", help="只重跑這份報告裡失敗或未執行的攝影機")
    apply.set_defaults(func=cmd_apply)

    get = commands.add_parser("get", help="讀取單一支攝影機的某個 ISAPI 路徑，用來確認欄位")
    get.add_argument("--host", required=True)
    get.add_argument("--path", required=True, help="例如 /ISAPI/System/deviceInfo")
    get.add_argument("--port", type=int)
    get.add_argument("--https", action="store_true")
    get.add_argument("--username", default="admin")
    get.add_argument("--password")
    get.add_argument("--password-env", default="HIK_PASSWORD")
    get.add_argument("--timeout", type=float, default=20)
    get.add_argument("--verify-tls", action="store_true")
    get.add_argument("--output", help="寫入檔案；未指定時印到標準輸出")
    get.set_defaults(func=cmd_get)
    return parser


def cmd_validate(args: argparse.Namespace) -> int:
    profile, selected, message = _prepare(args, load_profile(args.profile))
    if selected:
        resolve_cameras(
            selected,
            profile,
            password=args.password,
            password_env=args.password_env,
            username=args.username,
        )
    print("未連線，只檢查清單與設定檔")
    if message:
        print(message)
    return 0


def cmd_probe(args: argparse.Namespace) -> int:
    probe_step = Step(
        id="device_info",
        description="讀取裝置資訊",
        path="/ISAPI/System/deviceInfo",
        mode="request",
        method="GET",
        on_error="abort_camera",
        capture_body=True,
    )
    base = load_profile(args.profile) if args.profile else Profile(name="probe", steps=(probe_step,))
    profile = replace(base, name="probe", description="連線探測", steps=(probe_step,))
    prepared, selected, message = _prepare(args, profile)
    if not selected:
        print(message or "沒有需要執行的攝影機", file=sys.stderr)
        return 0
    report = _execute(args, prepared, selected, dry_run=False, kind="probe")
    return report.exit_code


def cmd_apply(args: argparse.Namespace) -> int:
    profile, selected, message = _prepare(args, load_profile(args.profile))
    if not selected:
        print(message or "沒有需要執行的攝影機", file=sys.stderr)
        return 0
    kind = "dry-run" if args.dry_run else "apply"
    report = _execute(args, profile, selected, dry_run=args.dry_run, kind=kind)
    return report.exit_code


def cmd_get(args: argparse.Namespace) -> int:
    password = args.password or os.environ.get(args.password_env) or ""
    if not password:
        raise ConfigError(f"請用 --password 或環境變數 {args.password_env} 提供密碼")
    if not str(args.path).startswith("/"):
        raise ConfigError("path 必須以 / 開頭")
    port = args.port if args.port else (443 if args.https else 80)
    try:
        with IsapiClient(
            host=args.host,
            port=port,
            username=args.username,
            password=password,
            use_https=args.https,
            verify_tls=args.verify_tls,
            timeout=args.timeout,
        ) as client:
            response = client.request("GET", args.path)
    except TransportError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    if args.output:
        destination = Path(args.output)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(response.body, encoding="utf-8")
        print(f"已寫入 {destination}", file=sys.stderr)
    else:
        sys.stdout.write(response.body)
        if response.body and not response.body.endswith("\n"):
            sys.stdout.write("\n")
    if not response.ok:
        print(response.error or "讀取失敗", file=sys.stderr)
        return 1
    return 0


def _execute(
    args: argparse.Namespace,
    profile: Profile,
    selected: list[Camera],
    *,
    dry_run: bool,
    kind: str,
) -> RunReport:
    action = "演練" if dry_run else "套用"
    print(
        f"開始{action} {profile.name}：{len(selected)} 支，{len(profile.steps)} 個步驟，並發 {profile.concurrency}",
        file=sys.stderr,
    )

    def on_progress(result: CameraResult, done: int, total: int) -> None:
        if args.quiet:
            return
        print(_format_progress(result, done, total), file=sys.stderr)

    def on_safety(message: str) -> None:
        print(message, file=sys.stderr)

    report = Runner(profile).run(
        selected,
        dry_run=dry_run,
        password=args.password,
        password_env=args.password_env,
        username=args.username,
        on_progress=on_progress,
        on_safety=on_safety,
    )
    output = Path(args.output) if args.output else _default_output(kind)
    json_path = report.write_json(output)
    if kind == "probe":
        csv_path = _write_probe_csv(report, json_path.with_suffix(".csv"))
    else:
        csv_path = report.write_csv(json_path.with_suffix(".csv"))
    print(format_summary(report))
    print(f"報告 {json_path}")
    print(f"清單 {csv_path}")
    return report


def _prepare(args: argparse.Namespace, profile: Profile) -> tuple[Profile, list[Camera], str | None]:
    profile = profile.with_overrides(
        concurrency=getattr(args, "concurrency", None),
        timeout_seconds=getattr(args, "timeout", None),
        retries=getattr(args, "retries", None),
        max_failure_ratio=getattr(args, "max_failure_ratio", None),
        safety_min_samples=getattr(args, "safety_samples", None),
    )
    cameras = load_inventory(args.inventory)
    for warning in inventory_warnings(cameras):
        print(f"警告：{warning}", file=sys.stderr)
    disabled = sum(1 for camera in cameras if not camera.enabled)
    only = _parse_only(args.only)
    retry = getattr(args, "retry_failed", None)
    message = None
    if retry:
        failed = load_failed_ids(retry)
        if not failed:
            message = "這份報告沒有失敗的攝影機"
        else:
            only = failed if only is None else only & failed
    selected: list[Camera] = []
    if message is None:
        selected = select_cameras(cameras, tags=args.tag or [], only=only, offset=args.offset, limit=args.limit)
        if not selected:
            message = "沒有需要執行的攝影機"
    print(f"清單共 {len(cameras)} 支（停用 {disabled} 支），符合條件 {len(selected)} 支", file=sys.stderr)
    step_names = "、".join(step.id for step in profile.steps)
    print(f"設定檔 {profile.name}，{len(profile.steps)} 個步驟：{step_names}", file=sys.stderr)
    ratio = profile.max_failure_ratio
    if ratio is None or ratio >= 1:
        protection = "失敗率保護已關閉"
    elif ratio <= 0:
        protection = f"失敗率保護：完成至少 {profile.safety_min_samples} 支後，只要有失敗就停止"
    else:
        protection = (
            f"失敗率保護：完成至少 {profile.safety_min_samples} 支後，失敗率達到 {ratio:.0%} 就停止"
        )
    print(
        f"並發 {profile.concurrency}，逾時 {profile.timeout_seconds:g} 秒，重試 {profile.retries} 次。{protection}",
        file=sys.stderr,
    )
    return profile, selected, message


def format_summary(report: RunReport) -> str:
    lines: list[str] = []
    if report.dry_run:
        lines.append("演練模式，會修改裝置的請求沒有送出")
    lines.append(f"成功 {report.success_count}")
    lines.append(f"失敗 {report.failed_count}")
    lines.append(f"略過 {report.skipped_count}")
    if report.safety_message:
        lines.append(report.safety_message)
    failures = [camera for camera in report.cameras if not camera.ok and not camera.skipped]
    for camera in failures[:30]:
        lines.append(f"{camera.id}  {camera.host}  {camera.failed_step or '-'}  {camera.error or ''}")
    if len(failures) > 30:
        lines.append(f"另外 {len(failures) - 30} 支失敗，請看報告檔")
    return "\n".join(lines)


def _format_progress(result: CameraResult, done: int, total: int) -> str:
    if result.skipped:
        mark = "略過"
    elif result.ok:
        mark = "成功"
    else:
        mark = "失敗"
    detail = ""
    if not result.ok:
        step = f" {result.failed_step}" if result.failed_step else ""
        detail = f"{step} {result.error or result.note or ''}".rstrip()
    suffix = f" {detail}" if detail else ""
    return f"[{done}/{total}] {mark} {result.id} {result.host}{suffix} ({result.elapsed_ms / 1000:.1f}s)"


def _write_probe_csv(report: RunReport, path: Path) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = [
        "id",
        "host",
        "ok",
        "model",
        "serial_number",
        "firmware_version",
        "firmware_released",
        "device_name",
        "mac_address",
        "device_type",
        "error",
    ]
    with path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for camera in report.cameras:
            info: dict[str, str] = {}
            for step in camera.steps:
                if step.body:
                    info = extract_device_info(step.body)
                    break
            writer.writerow(
                {
                    "id": camera.id,
                    "host": camera.host,
                    "ok": "true" if camera.ok else "false",
                    "model": info.get("model", ""),
                    "serial_number": info.get("serial_number", ""),
                    "firmware_version": info.get("firmware_version", ""),
                    "firmware_released": info.get("firmware_released", ""),
                    "device_name": info.get("device_name", ""),
                    "mac_address": info.get("mac_address", ""),
                    "device_type": info.get("device_type", ""),
                    "error": camera.error or "",
                }
            )
    return path


def _parse_only(value: str | None) -> set[str] | None:
    if value is None or not str(value).strip():
        return None
    ids = {part.strip() for part in str(value).split(",") if part.strip()}
    if not ids:
        raise ConfigError("--only 沒有有效的 id")
    return ids


def _default_output(kind: str) -> Path:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return Path("results") / f"{kind}-{stamp}.json"


def _add_target_args(parser: argparse.ArgumentParser, *, profile_required: bool) -> None:
    parser.add_argument("--inventory", required=True, help="攝影機清單 CSV")
    parser.add_argument("--profile", required=profile_required, help="ISAPI 設定檔 YAML")
    parser.add_argument("--username", help="清單沒寫帳號時使用")
    parser.add_argument("--password", help="清單沒寫密碼時使用。有寫的那一列仍以清單為準")
    parser.add_argument("--password-env", help="密碼環境變數名稱，覆寫設定檔的 password_env")


def _add_filter_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--tag", action="append", default=[], help="只跑含有這個標籤的攝影機，可重複，符合其中一個即可")
    parser.add_argument("--only", help="只跑這些 id，以逗號分隔")
    parser.add_argument("--limit", type=int, help="最多執行幾支，適合先小批驗證")
    parser.add_argument("--offset", type=int, default=0, help="從符合條件的清單往後跳過幾支")


def _add_run_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--concurrency", type=int, help="同時連線的攝影機數量，建議 20 到 40")
    parser.add_argument("--timeout", type=float, help="單次請求逾時秒數")
    parser.add_argument("--retries", type=int, help="連線失敗或裝置忙碌時的額外重試次數")
    parser.add_argument("--max-failure-ratio", type=float, help="0 到 1。填 1 代表關閉失敗率保護")
    parser.add_argument("--safety-samples", type=int, help="至少完成幾支之後才計算失敗率")
    parser.add_argument("--output", help="報告 JSON 路徑，同名的 CSV 會一起寫出")
    parser.add_argument("--quiet", action="store_true", help="不逐支印出進度")


if __name__ == "__main__":
    raise SystemExit(main())
