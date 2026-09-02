#!/usr/bin/env python3
"""
Load an EPC catalogue and generate movement documents through the public API.

Everything is driven by the file and the command line: product styles are
derived from the stock codes in the CSV, nothing about the data is assumed, and
no EPC, style, quantity or document number is written into this script.

    python scripts/load_catalogue.py --csv 400_EPC_with_Stock_Code.csv

The CSV needs two columns, EPC and Stock Code. EPCs may be written with or
without spaces. A stock code of the form "BR207 001" is read as style BR207,
roll 001; a code with no space is treated as its own style.

Rows whose EPC is not valid hexadecimal are reported and skipped rather than
guessed at -- a wrong EPC in the catalogue means a real roll alarms at the gate
for ever.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import os
import re
import sys
import urllib.error
import urllib.request
from collections import Counter

BOUNDARY = "----warehouse-catalogue-loader"


class Api:
    """Thin authenticated client for the warehouse API."""

    def __init__(self, base: str, user: str, password: str) -> None:
        self.base = base.rstrip("/")
        self.token: str | None = None
        self._login(user, password)

    def _request(self, method, path, body=None, raw=None, content_type="application/json"):
        headers = {}
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"

        if raw is not None:
            data, headers["Content-Type"] = raw, content_type
        elif body is not None:
            data, headers["Content-Type"] = json.dumps(body).encode(), content_type
        else:
            data = None

        request = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)

        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                text = response.read().decode()
                return response.status, (json.loads(text) if text else None)
        except urllib.error.HTTPError as error:
            detail = error.read().decode()
            try:
                detail = json.loads(detail)
            except ValueError:
                pass
            return error.code, detail

    def _login(self, user: str, password: str) -> None:
        status, body = self._request(
            "POST", "/api/auth/login", {"userName": user, "password": password})

        if status != 200 or not isinstance(body, dict):
            raise SystemExit(f"Login failed ({status}): {body}")

        self.token = body["token"]
        print(f"  signed in as {body['displayName']} ({', '.join(body['roles'])})")

    def get(self, path):
        return self._request("GET", path)

    def post(self, path, body=None):
        return self._request("POST", path, body=body)

    def upload_csv(self, path: str, rows: list[dict], columns: list[str]):
        """Posts rows as a multipart CSV to an import endpoint."""
        buffer = io.StringIO()
        writer = csv.DictWriter(buffer, fieldnames=columns, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)

        payload = (
            f"--{BOUNDARY}\r\n"
            'Content-Disposition: form-data; name="file"; filename="catalogue.csv"\r\n'
            "Content-Type: text/csv\r\n\r\n"
        ).encode() + buffer.getvalue().encode() + f"\r\n--{BOUNDARY}--\r\n".encode()

        return self._request(
            "POST", path, raw=payload,
            content_type=f"multipart/form-data; boundary={BOUNDARY}")


def normalise(value: str | None) -> str:
    """Uppercase, strip all whitespace. Matches the server's own rule."""
    return re.sub(r"\s+", "", value or "").upper()


def is_valid_epc(epc: str) -> bool:
    return bool(epc) and len(epc) % 2 == 0 and len(epc) <= 128 and re.fullmatch(r"[0-9A-F]+", epc) is not None


def read_catalogue(path: str):
    """Returns (accepted, rejected). Accepted entries are (epc, stock_code, style)."""
    accepted, rejected = [], []
    seen: set[str] = set()

    with open(path, encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)

        columns = {name.strip().lower(): name for name in (reader.fieldnames or [])}
        epc_column = columns.get("epc")
        code_column = columns.get("stock code") or columns.get("stockcode") or columns.get("code")

        if not epc_column:
            raise SystemExit(f"{path} has no EPC column (found: {reader.fieldnames})")

        for line, row in enumerate(reader, start=2):
            epc = normalise(row.get(epc_column))
            code = (row.get(code_column) or "").strip() if code_column else ""

            if not is_valid_epc(epc):
                rejected.append((line, epc or "(blank)", code, "not valid hexadecimal"))
                continue

            if epc in seen:
                rejected.append((line, epc, code, "duplicate within the file"))
                continue

            seen.add(epc)
            style = code.split()[0] if " " in code else (code or "UNASSIGNED")
            accepted.append((epc, code, style))

    return accepted, rejected


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--csv", required=True, help="EPC catalogue file")
    parser.add_argument("--api", default=os.environ.get("WAREHOUSE_API", "http://localhost:5080"))
    parser.add_argument("--user", default=os.environ.get("WAREHOUSE_USER", "admin"))
    parser.add_argument("--password", default=os.environ.get("WAREHOUSE_PASSWORD"))
    parser.add_argument("--inward", type=int, default=6, help="INWARD documents to create")
    parser.add_argument("--outward", type=int, default=6, help="OUTWARD documents to create")
    parser.add_argument("--per-document", type=int, default=30, help="EPCs per document")
    parser.add_argument("--uom", default="ROLL", help="Unit of measure for generated products")
    args = parser.parse_args()

    if not args.password:
        parser.error("--password is required (or set WAREHOUSE_PASSWORD)")

    print(f"Reading {args.csv}")
    accepted, rejected = read_catalogue(args.csv)
    print(f"  accepted {len(accepted)}, rejected {len(rejected)}")

    needed = (args.inward + args.outward) * args.per_document

    if len(accepted) < needed:
        raise SystemExit(
            f"Need {needed} usable EPCs for {args.inward + args.outward} documents "
            f"of {args.per_document}, but only {len(accepted)} are usable.")

    print(f"\nConnecting to {args.api}")
    api = Api(args.api, args.user, args.password)

    # Styles come from the data. Nothing here knows what a BR207 is.
    styles = sorted({style for _, _, style in accepted})
    print(f"\nStyles found in the file: {', '.join(styles)}")

    for style in styles:
        status, _ = api.post("/api/products", {
            "Code": style,
            "Name": f"{style} roll",
            "Uom": args.uom,
            "UnitsPerCarton": 1,
        })

        if status not in (200, 201):
            raise SystemExit(f"Could not create product {style}: HTTP {status}")

    print(f"  {len(styles)} product(s) ready")

    print("\nImporting EPCs")
    rows = [
        {
            "Epc": epc,
            "ItemCode": code,
            "ItemName": f"{style} roll {code.split()[-1]}" if " " in code else style,
            "CartonNumber": code,
            "ProductCode": style,
            "UnitQuantity": 1,
            "Status": "Registered",
        }
        for epc, code, style in accepted
    ]

    status, result = api.upload_csv(
        "/api/epcs/import", rows,
        ["Epc", "ItemCode", "ItemName", "CartonNumber", "ProductCode", "UnitQuantity", "Status"])

    if status != 200 or not isinstance(result, dict):
        raise SystemExit(f"Import failed ({status}): {result}")

    print(f"  imported {result['imported']}, updated {result['updated']}, rejected {len(result['errors'])}")

    for error in result["errors"][:10]:
        print(f"    line {error['row']}: {error['reason']}")

    # Deterministic split by stock code, so a re-run produces the same documents.
    ordered = sorted(accepted, key=lambda entry: (entry[1], entry[0]))
    inward_pool = ordered[: args.inward * args.per_document]
    outward_pool = ordered[args.inward * args.per_document: needed]

    # Outward can only ship what the warehouse already holds.
    print(f"\nSeating {len(outward_pool)} EPCs as in stock for the outward documents")
    status, result = api.upload_csv(
        "/api/epcs/import?updateExisting=true",
        [{"Epc": epc, "Status": "InStock"} for epc, _, _ in outward_pool],
        ["Epc", "Status"])

    if status != 200:
        raise SystemExit(f"Could not seat outward stock ({status}): {result}")

    print(f"  {result['updated']} updated")

    print("\nGenerating documents")
    created = []

    for kind, pool, count in (("inward", inward_pool, args.inward), ("outward", outward_pool, args.outward)):
        for index in range(count):
            chunk = pool[index * args.per_document:(index + 1) * args.per_document]
            styles_in_chunk = Counter(style for _, _, style in chunk)
            reference = "/".join(f"{style}x{n}" for style, n in styles_in_chunk.most_common())

            status, document = api.post(f"/api/documents/{kind}", {
                "epcs": [epc for epc, _, _ in chunk],
                "reference": reference[:120],
                "notes": f"Generated from {os.path.basename(args.csv)}",
            })

            if status != 201 or not isinstance(document, dict):
                raise SystemExit(f"Could not create {kind} document {index + 1}: HTTP {status} {document}")

            created.append(document)
            print(f"  {document['documentNumber']:<18} {document['type']:<8} "
                  f"{document['expectedArticles']:>3} articles  {reference}")

    print(f"\nDone: {len(created)} documents, {len(accepted)} EPCs, {len(styles)} styles.")

    if rejected:
        print(f"\n{len(rejected)} row(s) were not imported and need re-scanning:")
        for line, epc, code, reason in rejected:
            print(f"  line {line:>4}  {code:<14} {epc:<28} {reason}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
