"""Publish an existing, verified Windows CI artifact to a prepared GitHub release."""
import hashlib
import io
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
import zipfile


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected and urllib.parse.urlsplit(req.full_url).netloc != urllib.parse.urlsplit(newurl).netloc:
            redirected.remove_header("Authorization")
        return redirected


def main():
    repository = os.environ["GITHUB_REPOSITORY"]
    build_id = int(os.environ["ATIME_BUILD_RUN_ID"])
    release_id = int(os.environ["ATIME_RELEASE_ID"])
    base = "https://api.github.com/repos/" + repository
    headers = {
        "Authorization": "Bearer " + os.environ["GH_TOKEN"],
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2026-03-10",
        "User-Agent": "ATime-Release-Publisher",
    }
    opener = urllib.request.build_opener(SafeRedirect())

    def api(path, method="GET", payload=None):
        request = urllib.request.Request(base + path, headers=headers, method=method,
            data=None if payload is None else json.dumps(payload).encode())
        with opener.open(request, timeout=60) as response:
            data = response.read()
            return json.loads(data) if data else None

    run = api(f"/actions/runs/{build_id}")
    release = api(f"/releases/{release_id}")
    if not (
        run["conclusion"] == "success"
        and run["path"] == ".github/workflows/ci.yml"
        and run["event"] == "push"
        and run["head_branch"] == "main"
        and run["repository"]["id"] == run["head_repository"]["id"]
        and run["head_sha"] == release["target_commitish"]
    ):
        raise RuntimeError("Expected a successful main-branch Windows build of the release commit.")
    version = release["tag_name"]
    if not re.fullmatch(r"v\d+\.\d+\.\d+", version) or release["prerelease"]:
        raise RuntimeError("Expected a stable vX.Y.Z release.")

    data = api(f"/actions/runs/{build_id}/artifacts")
    artifacts = [a for a in data["artifacts"] if a["name"] == "ATime-win-x64" and not a["expired"]]
    if len(artifacts) != 1:
        raise RuntimeError("Expected one Windows build artifact.")
    artifact = artifacts[0]
    request = urllib.request.Request(artifact["archive_download_url"], headers=headers)
    with opener.open(request, timeout=120) as response:
        archive_bytes = response.read()
    if artifact.get("digest") != "sha256:" + hashlib.sha256(archive_bytes).hexdigest():
        raise RuntimeError("CI artifact SHA-256 mismatch.")

    package_name = f"ATime-{version}-win-x64.zip"
    checksum_name = package_name + ".sha256"
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        if sorted(archive.namelist()) != sorted([package_name, checksum_name]):
            raise RuntimeError("Unexpected files in build artifact.")
        files = {name: archive.read(name) for name in (package_name, checksum_name)}
    expected_hash = files[checksum_name].decode("ascii").split()[0]
    if hashlib.sha256(files[package_name]).hexdigest() != expected_hash:
        raise RuntimeError("Windows ZIP checksum mismatch.")
    with zipfile.ZipFile(io.BytesIO(files[package_name])) as package:
        names = package.namelist()
        if "ATime-win-x64/ATime.exe" not in names or package.testzip():
            raise RuntimeError("Windows runtime package is incomplete.")
        if any(name.lower().endswith((".db", ".db-wal", ".db-shm", ".sqlite", ".csv", ".log", ".pem", ".key")) for name in names):
            raise RuntimeError("Unexpected local data in Windows package.")

    assets = {a["name"]: a for a in release["assets"]}
    if set(assets) - set(files):
        raise RuntimeError("Draft contains unexpected attachments.")
    upload_url = release["upload_url"].split("{", 1)[0]
    if urllib.parse.urlsplit(upload_url).netloc != "uploads.github.com":
        raise RuntimeError("Unexpected upload host.")
    for name, content in files.items():
        digest = "sha256:" + hashlib.sha256(content).hexdigest()
        existing = assets.get(name)
        if existing and existing["state"] == "uploaded":
            if existing.get("digest") != digest or existing["size"] != len(content):
                raise RuntimeError("An existing attachment differs from the verified build.")
            continue
        if not release["draft"]:
            raise RuntimeError("Cannot add or replace attachments on an already published release.")
        if existing:
            # Remove only a failed partial upload of this exact expected attachment.
            api(f'/releases/assets/{existing["id"]}', "DELETE")
        upload_headers = dict(headers)
        upload_headers["Content-Type"] = "application/zip" if name.endswith(".zip") else "text/plain"
        request = urllib.request.Request(upload_url + "?name=" + urllib.parse.quote(name),
            data=content, headers=upload_headers, method="POST")
        with opener.open(request, timeout=180) as response:
            uploaded = json.load(response)
        if uploaded.get("digest") != digest or uploaded["size"] != len(content):
            raise RuntimeError("Uploaded attachment failed integrity verification.")
        print(f"Verified upload: {name} ({len(content)} bytes)")

    verified = api(f"/releases/{release_id}")
    remote = {a["name"]: a for a in verified["assets"]}
    if set(remote) != set(files):
        raise RuntimeError("Incomplete release attachment set.")
    for name, content in files.items():
        asset = remote[name]
        if asset["state"] != "uploaded" or asset["size"] != len(content) or asset.get("digest") != "sha256:" + hashlib.sha256(content).hexdigest():
            raise RuntimeError("Release attachment integrity check failed.")
    if verified["draft"]:
        verified = api(f"/releases/{release_id}", "PATCH", {"draft": False, "make_latest": "true"})
    tag = api("/git/ref/tags/" + version)
    if verified["draft"] or tag["object"]["sha"] != run["head_sha"]:
        raise RuntimeError("Published release tag does not match the verified source commit.")
    print("Published " + verified["html_url"])


if __name__ == "__main__":
    main()
