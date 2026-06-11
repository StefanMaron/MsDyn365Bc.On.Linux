## MacOS steps

* Podman needs to run in "rootful" mode. 

  `podman machine set --rootful`

* You might need to enable rosetta: 

  `podman machine ssh 'sudo touch /etc/containers/enable-rosetta'`

* Make sure that the _~/.config/containers/containers.conf_ contains:
  ```
    [machine]
    provider = "applehv"
  ```

* You will need at least 15G to run BC. 
  
  `podman machine set --memory 15000`

* You will need to install docker-compose, podman compose does not support "wait"

  `brew install docker-compose`

## Starting BC

Use the macOS overlay on top of the main compose file. It pins SQL Server
to 2022-CU17 (later 2022 CUs crash under Rosetta emulation) and runs the
SQL container as root (rootful podman mounts the data tmpfs root-owned,
which the image's non-root mssql user cannot write to):

```bash
docker compose -f docker-compose.yml -f docker-compose.macos.yml up -d --wait
```

Everything else (ports, env vars like `BC_VERSION`/`BC_COUNTRY`, running
tests via `scripts/run-tests.sh`) works the same as on Linux.

