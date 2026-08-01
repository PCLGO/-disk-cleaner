#!/usr/bin/env python3
"""Fast, read-only cleanup candidate inventory for Windows drives."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import re
import sys
import time
from collections import defaultdict, deque
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable


DIRECTORY_RULES = {
    "temp": ("low", "temporary-data", "临时目录，内容通常可重建，但应先关闭相关应用"),
    "tmp": ("low", "temporary-data", "临时目录，内容通常可重建，但应先关闭相关应用"),
    "cache": ("medium", "application-cache", "通用缓存目录；仅凭名称不能确定可安全删除"),
    "caches": ("medium", "application-cache", "通用缓存目录；仅凭名称不能确定可安全删除"),
    "gpucache": ("low", "application-cache", "GPU 渲染缓存，关闭所属应用后通常可重建"),
    "code cache": ("low", "application-cache", "代码缓存，关闭所属应用后通常可重建"),
    "shadercache": ("medium", "application-cache", "着色器缓存，可重建但可能导致重新编译"),
    "crashdumps": ("medium", "diagnostics", "崩溃转储；删除会丢失诊断信息"),
    "logs": ("medium", "logs", "日志目录；可能仍用于诊断或审计"),
    "$recycle.bin": ("medium", "recycle-bin", "回收站内容；应由用户确认后通过 Windows 清空"),
    "windows.old": ("high", "old-system", "旧 Windows 安装；应通过 Windows 存储清理"),
    "node_modules": ("medium", "developer-cache", "依赖可重装但重建可能很慢，也可能包含本地修改"),
    ".venv": ("medium", "developer-cache", "Python 虚拟环境通常可重建，但需确认依赖可恢复"),
    "venv": ("medium", "developer-cache", "Python 虚拟环境通常可重建，但需确认依赖可恢复"),
}

STALE_FILE_SUFFIXES = {
    ".tmp": ("low", "temporary-file", 14),
    ".dmp": ("medium", "diagnostics", 30),
    ".log": ("medium", "logs", 90),
    ".bak": ("high", "possible-backup", 180),
    ".old": ("high", "possible-backup", 180),
    ".iso": ("high", "large-archive", 180),
    ".msi": ("high", "installer", 180),
}

PROTECTED_PARTS = {
    "system32",
    "winsxs",
    "windows\\installer",
    "program files",
    "program files (x86)",
    "system volume information",
    ".git",
}

USER_LIBRARY_PARTS = {
    "desktop",
    "documents",
    "downloads",
    "pictures",
    "music",
    "videos",
    "onedrive",
}


@dataclass
class Candidate:
    id: str
    drive: str
    kind: str
    path: str
    size_bytes: int
    risk: str
    category: str
    reason: str
    modified_utc: str | None
    identity: str = ""
    suspected_purpose: str = ""
    evidence: str = ""
    confidence: str = "low"
    deletion_impact: str = ""
    recommended_action: str = "进一步只读检查"
    size_complete: bool = True
    paths: list[str] | None = None


class Budget:
    def __init__(self, seconds: float, max_entries: int):
        self.started = time.monotonic()
        self.deadline = self.started + seconds
        self.max_entries = max_entries
        self.entries = 0
        self.hit_time_limit = False
        self.hit_entry_limit = False

    def allow(self) -> bool:
        if time.monotonic() >= self.deadline:
            self.hit_time_limit = True
            return False
        if self.entries >= self.max_entries:
            self.hit_entry_limit = True
            return False
        return True

    def count(self) -> bool:
        if not self.allow():
            return False
        self.entries += 1
        return True


def normalize_drive(value: str) -> Path:
    value = value.strip().rstrip("\\/")
    if re.fullmatch(r"[A-Za-z]:", value):
        return Path(value.upper() + "\\")
    path = Path(value).resolve()
    if not path.is_absolute():
        raise argparse.ArgumentTypeError(f"Expected a drive root or absolute directory, got: {value}")
    return path


def lower_path(path: Path) -> str:
    return str(path).replace("/", "\\").casefold()


def is_protected(path: Path) -> bool:
    normalized = lower_path(path)
    parts = {part.casefold() for part in path.parts}
    if any(token in normalized for token in PROTECTED_PARTS):
        return True
    if "users" in parts and parts.intersection(USER_LIBRARY_PARTS):
        return True
    return False


def iso_mtime(stat_result: os.stat_result) -> str:
    return datetime.fromtimestamp(stat_result.st_mtime, timezone.utc).isoformat()


def owner_hint(path: Path) -> str:
    ignored = {
        "cache",
        "caches",
        "logs",
        "temp",
        "tmp",
        "crashdumps",
        "node_modules",
        "appdata",
        "local",
        "roaming",
        "users",
    }
    for part in reversed(path.parts[:-1]):
        if part.casefold() not in ignored and not re.fullmatch(r"[A-Za-z]:\\?", part):
            return part
    return "未知软件"


def is_bundled_app_dependency(path: Path) -> bool:
    normalized = lower_path(path)
    return (
        "\\resources\\app\\node_modules" in normalized
        or "\\program files\\" in normalized
        or "\\program files (x86)\\" in normalized
    )


def describe_candidate(path: Path, category: str, reason: str) -> dict[str, str]:
    normalized = lower_path(path)
    owner = owner_hint(path)
    suffix = path.suffix.casefold()

    if "crashdumps" in normalized:
        return {
            "identity": "当前 Windows 用户的应用崩溃转储目录",
            "suspected_purpose": "保存应用崩溃时的内存转储，供故障诊断",
            "evidence": "路径位于 AppData\\Local\\CrashDumps，符合 Windows Error Reporting 约定",
            "confidence": "high",
            "deletion_impact": "删除后不会修复应用，但会失去已有崩溃的诊断材料",
            "recommended_action": "确认近期不需要排查崩溃后，可考虑清理旧转储",
        }
    if "\\appdata\\local\\temp" in normalized or normalized.endswith("\\windows\\temp"):
        return {
            "identity": "Windows 或当前用户的临时文件目录",
            "suspected_purpose": "应用安装、更新和运行期间产生的临时工作文件",
            "evidence": "路径是 Windows 约定的 Temp 位置",
            "confidence": "high",
            "deletion_impact": "多数内容可重建；正在使用的文件不能删，少数未完成任务可能依赖其中内容",
            "recommended_action": "关闭应用后使用 Windows 临时文件清理，并跳过占用项",
        }
    if "\\pip\\cache" in normalized:
        return {
            "identity": "Python pip 下载与构建缓存",
            "suspected_purpose": "保存已下载的 Python 包和构建产物以加速再次安装",
            "evidence": "路径符合 pip 的用户缓存目录约定",
            "confidence": "high",
            "deletion_impact": "不会卸载现有 Python 包，但以后安装可能重新下载",
            "recommended_action": "优先运行 pip cache info，再按需使用 pip cache purge",
        }
    if "npm-cache" in normalized or "\\.npm\\" in normalized:
        return {
            "identity": "npm 包管理器缓存",
            "suspected_purpose": "保存 npm 下载包、索引和校验数据",
            "evidence": "路径名称和位置符合 npm 用户缓存约定",
            "confidence": "high",
            "deletion_impact": "不会删除项目源码；后续 npm 安装可能重新联网下载",
            "recommended_action": "先运行 npm cache verify；确认需要释放空间后使用 npm 官方清理命令",
        }
    if normalized.endswith("\\.cache"):
        return {
            "identity": "当前用户的通用 .cache 容器",
            "suspected_purpose": "可能由多个开发工具、模型工具或桌面应用共同使用",
            "evidence": "位于用户主目录，但仅凭 .cache 名称无法确认其中每个子目录的归属",
            "confidence": "medium",
            "deletion_impact": "不同子目录影响不同，可能触发大文件重新下载或丢失离线缓存",
            "recommended_action": "先按第一层子目录统计并识别所属软件，不要整目录清理",
        }
    if category == "developer-cache" and path.name.casefold() == "node_modules":
        return {
            "identity": f"{owner} 下的 JavaScript 依赖目录",
            "suspected_purpose": "可能是项目依赖，也可能是已安装应用的运行组件",
            "evidence": "目录名为 node_modules；需要结合父目录中的 package.json 和安装位置判断",
            "confidence": "medium",
            "deletion_impact": "项目依赖可重装但有时间成本；应用内置依赖被删会导致程序损坏",
            "recommended_action": "先确认父目录是源码项目且锁文件完整；应用安装目录不处理",
        }
    if category == "logs":
        return {
            "identity": f"{owner} 产生的日志目录",
            "suspected_purpose": "记录运行、更新、错误或诊断信息",
            "evidence": f"目录名为 logs，父级路径指向 {owner}",
            "confidence": "medium",
            "deletion_impact": "通常不影响主数据，但会丢失历史诊断和审计信息",
            "recommended_action": "确认所属软件及日志保留需求后，仅清理旧日志",
        }
    if category == "recycle-bin":
        return {
            "identity": "该盘符的 Windows 回收站存储",
            "suspected_purpose": "保存用户已删除但尚可恢复的文件",
            "evidence": "目录名为 $RECYCLE.BIN，是 Windows 标准回收站目录",
            "confidence": "high",
            "deletion_impact": "清空后其中内容无法再从回收站恢复",
            "recommended_action": "通过 Windows 回收站界面查看内容并由用户清空",
        }
    if suffix == ".msi":
        package_cache = "\\programdata\\package cache\\" in normalized
        return {
            "identity": "软件安装器的 MSI 安装包" if not package_cache else "软件修复/卸载使用的 Package Cache 安装包",
            "suspected_purpose": "独立下载的安装程序" if not package_cache else "已安装软件保留的修复、升级或卸载源文件",
            "evidence": "扩展名为 .msi" + ("，且位于 ProgramData\\Package Cache" if package_cache else ""),
            "confidence": "high" if package_cache else "medium",
            "deletion_impact": "可能导致对应软件无法修复、升级或卸载" if package_cache else "若软件已安装且不再需要安装器，删除通常只失去离线重装副本",
            "recommended_action": "不直接删除；通过卸载对应软件或官方清理工具处理" if package_cache else "先核对文件名、数字签名和软件是否仍需离线安装",
        }
    if suffix in {".iso", ".bak", ".old"}:
        return {
            "identity": f"{suffix} 文件",
            "suspected_purpose": "可能是磁盘镜像、备份或旧版本副本",
            "evidence": f"依据扩展名 {suffix} 和长期未修改时间推断，尚未读取内容",
            "confidence": "low",
            "deletion_impact": "可能失去唯一备份、安装介质或历史版本",
            "recommended_action": "先确认来源、是否存在其他副本及最后使用场景",
        }
    return {
        "identity": f"{owner} 路径下的 {category}",
        "suspected_purpose": reason,
        "evidence": f"主要依据路径名称和规则分类：{category}",
        "confidence": "low",
        "deletion_impact": "用途尚未充分确认，删除影响未知",
        "recommended_action": "继续查看第一层内容和所属软件元数据后再判断",
    }


def tree_size(root: Path, budget: Budget) -> tuple[int, bool, int]:
    total = 0
    denied = 0
    stack = [root]
    complete = True
    while stack and budget.allow():
        current = stack.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    if not budget.count():
                        complete = False
                        break
                    try:
                        if entry.is_symlink():
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(Path(entry.path))
                        elif entry.is_file(follow_symlinks=False):
                            total += entry.stat(follow_symlinks=False).st_size
                    except (FileNotFoundError, PermissionError, OSError):
                        denied += 1
        except (FileNotFoundError, PermissionError, OSError):
            denied += 1
        if not budget.allow():
            complete = False
    if stack:
        complete = False
    return total, complete, denied


def known_locations(drive: Path) -> Iterable[tuple[Path, str, str, str]]:
    drive_letter = drive.drive.upper()
    if drive_letter == "C:":
        env_paths = [
            os.environ.get("TEMP"),
            os.environ.get("TMP"),
            str(Path(os.environ.get("LOCALAPPDATA", "")) / "CrashDumps"),
            str(Path(os.environ.get("LOCALAPPDATA", "")) / "pip" / "Cache"),
            str(Path(os.environ.get("LOCALAPPDATA", "")) / "npm-cache"),
            str(Path(os.environ.get("USERPROFILE", "")) / ".cache"),
            str(Path(os.environ.get("USERPROFILE", "")) / ".gradle" / "caches"),
            str(Path(os.environ.get("USERPROFILE", "")) / ".nuget" / "packages"),
            r"C:\Windows\Temp",
            r"C:\Windows\SoftwareDistribution\Download",
        ]
        for raw in env_paths:
            if raw:
                path = Path(raw)
                if path.drive.upper() == drive_letter:
                    yield path, "medium", "known-cache", "已知缓存或临时位置；优先使用所属应用或 Windows 的清理入口"


def make_candidate(
    drive: Path,
    path: Path,
    risk: str,
    category: str,
    reason: str,
    size: int,
    complete: bool,
) -> Candidate:
    try:
        modified = iso_mtime(path.stat())
    except OSError:
        modified = None
    description = describe_candidate(path, category, reason)
    return Candidate(
        id="",
        drive=drive.drive.upper() or str(drive),
        kind="directory",
        path=str(path),
        size_bytes=size,
        risk=risk,
        category=category,
        reason=reason,
        modified_utc=modified,
        **description,
        size_complete=complete,
    )


def scan_drive(
    drive: Path,
    budget: Budget,
    min_bytes: int,
    duplicate_files: list[tuple[Path, int]],
) -> tuple[list[Candidate], int]:
    candidates: list[Candidate] = []
    denied = 0
    claimed: set[str] = set()

    known = known_locations(drive) if str(drive) == drive.anchor else []
    for path, risk, category, reason in known:
        if not budget.allow() or not path.exists() or is_protected(path):
            continue
        key = lower_path(path)
        if key in claimed:
            continue
        claimed.add(key)
        size, complete, misses = tree_size(path, budget)
        denied += misses
        if size >= min_bytes:
            candidates.append(make_candidate(drive, path, risk, category, reason, size, complete))

    queue: deque[Path] = deque([drive])
    while queue and budget.allow():
        current = queue.popleft()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    if not budget.count():
                        break
                    path = Path(entry.path)
                    try:
                        if entry.is_symlink() or is_protected(path):
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            key = entry.name.casefold()
                            if key in DIRECTORY_RULES:
                                if key == "node_modules" and is_bundled_app_dependency(path):
                                    continue
                                if lower_path(path) not in claimed:
                                    risk, category, reason = DIRECTORY_RULES[key]
                                    size, complete, misses = tree_size(path, budget)
                                    denied += misses
                                    if size >= min_bytes:
                                        candidates.append(
                                            make_candidate(drive, path, risk, category, reason, size, complete)
                                        )
                                    claimed.add(lower_path(path))
                            else:
                                queue.append(path)
                        elif entry.is_file(follow_symlinks=False):
                            stat_result = entry.stat(follow_symlinks=False)
                            size = stat_result.st_size
                            if size >= min_bytes:
                                duplicate_files.append((path, size))
                                suffix = path.suffix.casefold()
                                if suffix in STALE_FILE_SUFFIXES:
                                    risk, category, min_age_days = STALE_FILE_SUFFIXES[suffix]
                                    age_days = (time.time() - stat_result.st_mtime) / 86400
                                    if age_days >= min_age_days:
                                        file_reason = (
                                            f"{suffix} 文件已约 {age_days:.0f} 天未修改；"
                                            "用途未知，需人工判断"
                                        )
                                        description = describe_candidate(path, category, file_reason)
                                        candidates.append(
                                            Candidate(
                                                id="",
                                                drive=drive.drive.upper() or str(drive),
                                                kind="file",
                                                path=str(path),
                                                size_bytes=size,
                                                risk=risk,
                                                category=category,
                                                reason=file_reason,
                                                modified_utc=iso_mtime(stat_result),
                                                **description,
                                            )
                                        )
                    except (FileNotFoundError, PermissionError, OSError):
                        denied += 1
        except (FileNotFoundError, PermissionError, OSError):
            denied += 1
    return candidates, denied


def sha256_file(path: Path, budget: Budget) -> str | None:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            while budget.allow():
                block = handle.read(1024 * 1024)
                if not block:
                    return digest.hexdigest()
                digest.update(block)
    except (FileNotFoundError, PermissionError, OSError):
        return None
    return None


def find_duplicates(files: list[tuple[Path, int]], budget: Budget) -> list[Candidate]:
    by_size: dict[int, list[Path]] = defaultdict(list)
    for path, size in files:
        by_size[size].append(path)

    groups: dict[tuple[int, str], list[Path]] = defaultdict(list)
    for size, paths in sorted(by_size.items(), reverse=True):
        if len(paths) < 2 or not budget.allow():
            continue
        for path in paths:
            digest = sha256_file(path, budget)
            if digest:
                groups[(size, digest)].append(path)

    results = []
    for (size, digest), paths in groups.items():
        if len(paths) < 2:
            continue
        description = {
            "identity": "内容完全相同的大文件组",
            "suspected_purpose": "可能是重复下载、复制或备份，但各副本所在位置的用途可能不同",
            "evidence": f"文件大小相同且 SHA-256 完全一致：{digest}",
            "confidence": "high",
            "deletion_impact": "删除指定副本不会改变其他副本内容，但路径引用、项目或备份策略可能依赖该副本",
            "recommended_action": "列出全部路径，由用户指定保留和删除的副本",
        }
        results.append(
            Candidate(
                id="",
                drive=",".join(sorted({p.drive.upper() for p in paths})),
                kind="duplicate_group",
                path=str(paths[0]),
                paths=[str(path) for path in paths],
                size_bytes=size * (len(paths) - 1),
                risk="high",
                category="exact-duplicate",
                reason=f"SHA-256 完全一致（{digest[:12]}…）；潜在空间假设只保留一份，需用户指定删除副本",
                modified_utc=None,
                **description,
                size_complete=True,
            )
        )
    return results


def assign_ids(candidates: list[Candidate]) -> None:
    risk_order = {"low": 0, "medium": 1, "high": 2}
    candidates.sort(key=lambda item: (-item.size_bytes, risk_order.get(item.risk, 9), item.path.casefold()))
    counters: dict[str, int] = defaultdict(int)
    for candidate in candidates:
        prefix = candidate.drive[:1] if candidate.drive[:1].isalpha() else "X"
        counters[prefix] += 1
        candidate.id = f"{prefix}{counters[prefix]:03d}"


def write_csv(path: Path, candidates: list[Candidate]) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=[
                "id",
                "drive",
                "kind",
                "path",
                "size_bytes",
                "risk",
                "category",
                "reason",
                "identity",
                "suspected_purpose",
                "evidence",
                "confidence",
                "deletion_impact",
                "recommended_action",
                "modified_utc",
                "size_complete",
                "paths",
            ],
        )
        writer.writeheader()
        for item in candidates:
            row = asdict(item)
            row["paths"] = " | ".join(item.paths or [])
            writer.writerow(row)


def main() -> int:
    parser = argparse.ArgumentParser(description="Read-only Windows cleanup candidate scanner")
    parser.add_argument(
        "drives",
        nargs="+",
        type=normalize_drive,
        help="Drive roots such as C: D:, or absolute directories for a focused scan",
    )
    parser.add_argument("--output", type=Path, default=Path("disk-cleaner-report.json"))
    parser.add_argument("--csv", type=Path, help="Optional UTF-8 CSV output")
    parser.add_argument("--min-size-mb", type=int, default=50)
    parser.add_argument("--max-seconds", type=float, default=45)
    parser.add_argument("--max-entries", type=int, default=80000)
    parser.add_argument("--duplicates", action="store_true", help="Hash same-size large files")
    args = parser.parse_args()

    drives = []
    for drive in args.drives:
        if drive.exists() and drive not in drives:
            drives.append(drive)
    if not drives:
        parser.error("None of the requested drives exist")

    budget = Budget(max(1, args.max_seconds), max(100, args.max_entries))
    min_bytes = max(1, args.min_size_mb) * 1024 * 1024
    all_candidates: list[Candidate] = []
    duplicate_files: list[tuple[Path, int]] = []
    denied = 0

    for drive in drives:
        if not budget.allow():
            break
        candidates, misses = scan_drive(drive, budget, min_bytes, duplicate_files)
        all_candidates.extend(candidates)
        denied += misses

    if args.duplicates and budget.allow():
        all_candidates.extend(find_duplicates(duplicate_files, budget))

    assign_ids(all_candidates)
    elapsed = time.monotonic() - budget.started
    totals: dict[str, int] = defaultdict(int)
    for candidate in all_candidates:
        totals[candidate.risk] += candidate.size_bytes

    report = {
        "schema_version": 2,
        "read_only": True,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "requested_drives": [str(drive) for drive in drives],
        "settings": {
            "min_size_mb": args.min_size_mb,
            "max_seconds": args.max_seconds,
            "max_entries": args.max_entries,
            "duplicates": args.duplicates,
        },
        "scan": {
            "elapsed_seconds": round(elapsed, 2),
            "entries_examined": budget.entries,
            "permission_or_io_skips": denied,
            "complete": not (budget.hit_time_limit or budget.hit_entry_limit),
            "hit_time_limit": budget.hit_time_limit,
            "hit_entry_limit": budget.hit_entry_limit,
        },
        "potential_bytes_by_risk": dict(totals),
        "candidate_count": len(all_candidates),
        "candidates": [asdict(candidate) for candidate in all_candidates],
    }

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.csv:
        args.csv.parent.mkdir(parents=True, exist_ok=True)
        write_csv(args.csv, all_candidates)

    print(f"Read-only report: {args.output.resolve()}")
    print(f"Candidates: {len(all_candidates)} | Entries: {budget.entries} | Elapsed: {elapsed:.2f}s")
    print(f"Complete within configured limits: {report['scan']['complete']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
