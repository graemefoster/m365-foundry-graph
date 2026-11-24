#!/usr/bin/env python3
"""Generate clearly labelled, reproducible fictional retrieval documents."""

import argparse
import csv
import hashlib
import json
import random
from pathlib import Path

import tomllib

ROOT = Path(__file__).resolve().parent
SCENARIO = ROOT / "scenario" / "articles.toml"


def generate(seed: int) -> dict[str, bytes]:
    scenario = tomllib.loads(SCENARIO.read_text())
    rng = random.Random(seed)
    rows = []
    tests = []
    files = {}
    for article in scenario["articles"]:
        price = f"{rng.randrange(1500, 18000) / 100:.2f}"
        inventory = rng.randrange(20, 400)
        title = f"SYNTHETIC Caldova: {article['title']}"
        description = (
            "SYNTHETIC DEMO DATA. This fictional training article is not a real "
            "manufacturer policy, repair instruction, diagnosis, warranty, or service commitment.\n\n"
            + article["body"].strip()
            + "\n\n"
            + scenario["common_context"].strip()
        )
        count = len(description.split())
        if not 300 <= count <= 500:
            raise ValueError(f"Article {article['id']} has {count} words; expected 300-500.")
        row = {
            "PartNumber": article["id"],
            "Name": title,
            "Description": description,
            "Price": price,
            "Inventory": inventory,
            "Appliances": article["model"],
        }
        rows.append(row)
        text = (
            f"{title}\nPart number: {article['id']}\n{description}\n"
            f"Price: {price.rstrip('0').rstrip('.')}\nInventory: {inventory}\nAppliances: {article['model']}"
        )
        files[f"parts/{article['id']}.txt"] = text.encode()
        tests.append({
            "name": article["slug"],
            "query": article["query"],
            "filter": None,
            "expectedIds": [str(article["id"])],
        })
    tests.append({
        "name": "no-match",
        "query": "What is the process for a fictional interplanetary appliance exchange?",
        "filter": "partNumber:999999",
        "expectedIds": [],
    })
    import io
    stream = io.StringIO(newline="")
    writer = csv.DictWriter(stream, fieldnames=list(rows[0]), lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    files["SemanticApplianceParts.csv"] = stream.getvalue().encode()
    files["retrieval-cases.json"] = (json.dumps(tests, indent=2) + "\n").encode()
    files["articles.json"] = (json.dumps(rows, indent=2) + "\n").encode()
    manifest = {
        "synthetic": True,
        "seed": seed,
        "articleCount": len(rows),
        "scenarioSha256": hashlib.sha256(SCENARIO.read_bytes()).hexdigest(),
        "anchors": {article["slug"]: str(article["id"]) for article in scenario["articles"]},
        "files": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())},
    }
    files["manifest.json"] = (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode()
    return files


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seed", type=int, default=20261109)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    files = generate(args.seed)
    generated = ROOT / "generated"
    if args.check:
        for name, data in files.items():
            path = generated / name
            if not path.is_file() or path.read_bytes() != data:
                raise SystemExit(f"Missing or changed generated file: {path}")
        print(f"Verified {len(files)} generated files, seed {args.seed}.")
        return
    for name, data in files.items():
        path = generated / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print(f"Generated 8 synthetic articles and 9 retrieval cases, seed {args.seed}.")


if __name__ == "__main__":
    main()
