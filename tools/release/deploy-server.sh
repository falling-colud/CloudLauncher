#!/bin/bash
# Deploys a linux-arm64 server publish to the server: dump the database, tar-pipe the publish into
# server.new, copy the live appsettings across, then stop, swap (keeping the old folder as
# server.prev-<stamp>), chcon, start and check routing (a registered route answers 401, not 404).
# chcon is needed after every upload: without the bin_t label SELinux blocks the binary (203/EXEC).
#
# Needs CL_DEPLOY_HOST (user@host), CL_DEPLOY_KEY (ssh key file) and CL_DEPLOY_DIR (the folder that
# holds server/, backups/ and the env file). The publish folder defaults to artifacts/server-publish:
#   dotnet publish CloudLauncher.Server -c Release -r linux-arm64 --self-contained -o artifacts/server-publish
set -euo pipefail
SP="${SP:-$(dirname "$0")/../../artifacts/server-publish}"
HOST="${CL_DEPLOY_HOST:?set CL_DEPLOY_HOST to user@host}"
KEY="${CL_DEPLOY_KEY:?set CL_DEPLOY_KEY to the ssh key file}"
BASE="${CL_DEPLOY_DIR:?set CL_DEPLOY_DIR to the server folder}"
STAMP=$(date +%Y%m%d-%H%M)
test -f "$SP/CloudLauncher.Server" || { echo "no publish output in $SP"; exit 1; }

echo "== pg_dump first (DB is small)"
ssh -i "$KEY" "$HOST" "mkdir -p $BASE/backups && docker exec cloudlauncher-postgres pg_dump -U \$(docker exec cloudlauncher-postgres printenv POSTGRES_USER) -d \$(docker exec cloudlauncher-postgres printenv POSTGRES_DB) --format=custom > $BASE/backups/db-$STAMP.dump && ls -la $BASE/backups/db-$STAMP.dump"

echo "== upload"
ssh -i "$KEY" "$HOST" "rm -rf $BASE/server.new && mkdir -p $BASE/server.new"
tar -C "$SP" -cf - . | ssh -i "$KEY" "$HOST" "tar -C $BASE/server.new -xf -"
ssh -i "$KEY" "$HOST" "cp -a $BASE/server/appsettings*.json $BASE/server.new/ 2>/dev/null; chmod +x $BASE/server.new/CloudLauncher.Server; ls $BASE/server.new | wc -l"

echo "== swap"
ssh -i "$KEY" "$HOST" "sudo systemctl stop cloudlauncher && rm -rf $BASE/server.prev-$STAMP && mv $BASE/server $BASE/server.prev-$STAMP && mv $BASE/server.new $BASE/server && sudo chcon -R -t bin_t $BASE/server && sudo systemctl start cloudlauncher && sleep 4 && systemctl is-active cloudlauncher"

echo "== prove"
ssh -i "$KEY" "$HOST" "curl -s -o /dev/null -w 'latest %{http_code}\n' http://127.0.0.1:5000/launcher/latest; curl -s -o /dev/null -w 'browse %{http_code}\n' 'http://127.0.0.1:5000/packs/browse?source=Public'; ls -la --time-style=full-iso $BASE/server/CloudLauncher.Server.dll; journalctl -u cloudlauncher --since '2 min ago' --no-pager | tail -5"
