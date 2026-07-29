# Single-node OpenBao with file storage — secrets survive container restarts.
# For HA production, prefer Raft or an external managed Vault/OpenBao instead.
ui = true
disable_mlock = true

storage "file" {
  path = "/openbao/data"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = "true"
}

api_addr     = "http://127.0.0.1:8200"
cluster_addr = "http://127.0.0.1:8201"
