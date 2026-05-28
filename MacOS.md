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

