### Operations Manual: Update Node and Backfill

This manual describes the procedure for a rolling update of a Nethermind node with the Circles Indexer plugin, including a partial data reset (backfill) to ensure data consistency.

#### Prerequisites
*   Access to the node's hosting environment (e.g., Docker, Kubernetes).
*   Access to the Postgres database used by the plugin (see `DATABASE_URL` in `.env`).
*   `curl`, `jq` and `watch` installed on your local machine or a management server.

---

### Step-by-Step Procedure

#### 1. Remove the first node from the Load Balancer
Ensure no traffic is routed to the node during the maintenance window to prevent request failures.

#### 2. Stop the Nethermind node
Shut down the Nethermind process cleanly. If using Docker:
```bash
docker stop <nethermind-container-name>
```

#### 3. Delete blocks from the database
Execute the following SQL to generate the deletion statements for all relevant tables. The Circles Indexer plugin stores events across multiple tables, all of which must be rolled back to the target block.

**Target Block:** `43563601`

Run this query in your Postgres client (e.g., `psql` or DBeaver):
```sql
SELECT 'DELETE FROM "' || t.table_name || '" WHERE "blockNumber" >= 43563601;'
FROM information_schema.tables t
JOIN information_schema.columns c ON c.table_name = t.table_name 
                                 AND c.table_schema = t.table_schema 
                                 AND c.table_catalog = t.table_catalog
WHERE t.table_catalog = 'postgres' -- Adjust if your DB name is different
AND t.table_schema = 'public'
AND t.table_type = 'BASE TABLE'
AND c.column_name IN ('blockNumber')
GROUP BY t.table_name;
```
**Important:** Copy the output of the query and execute the resulting `DELETE` statements. This ensures that the plugin's `StateMachine` correctly detects the new state and triggers the necessary re-indexing.

#### 4. Update the image version
Update your deployment configuration (e.g., `docker-compose.yml` or K8s manifest) to point to the latest release of the `circles-nethermind-plugin`.

You can find all image versions on [https://hub.docker.com/u/jaensen](https://hub.docker.com/u/jaensen):
* [jaensen/nethermind-circlesubi](https://hub.docker.com/r/jaensen/nethermind-circlesubi/tags)
* [jaensen/pathfinder-host](https://hub.docker.com/r/jaensen/pathfinder-host/tags)

#### 5. Start the node again
Restart the Nethermind node
```bash
docker start <nethermind-container-name>

# If you updated the image in docker-compose, recreate the container with:
docker comppose up -d
```

#### 6. Let it sync and backfill
Upon startup, the plugin will:
1.  Detect the current state of the database.
2.  Initialize in-memory caches (warming up).
3.  Transition to the `Reorg` state if it detects discrepancies or if the head was manually rolled back.
4.  Begin syncing missing blocks from the chain.

#### 7. Monitor health and logs
Monitor the initialization and sync progress using the `circles_health` RPC method. A "Healthy" status indicates that the indexer is within 3 blocks of the chain head.

**Important:** You must query the **restarted node directly**, not the load balancer, to ensure you are seeing the status of the node you are currently updating.

**Accessing the node:**
*   **Docker:** Usually accessible on `localhost:8545` (if ports are mapped).
*   **Kubernetes:** Create a tunnel to the specific pod:
    ```bash
    kubectl port-forward <pod-name> 8545:8545
    ```

**Monitor Loop:**
```bash
watch -n 2 "curl -s -X POST -H 'Content-Type: application/json' \
  --data '{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"circles_health\",\"params\":[]}' \
  http://localhost:8545 | jq"
```

**Known Initialization Issue:**
Check the logs for errors containing the string `monotonic` (e.g., `Block number must be monotonically increasing`).
*   **Cause:** This happens if the plugin initialization takes too long and the `RollbackCache` buffer runs low/fills up before the node is fully ready.
*   **Solution:** Restart the node. The plugin usually succeeds after a clean restart once the initial sync has progressed.

#### 8. Bring node back into LB pool
Once `circles_health` returns `"Healthy"`, the node is ready to serve traffic. Re-enable it in your load balancer.

#### 9. Repeat for the next node
Continue the rolling release process for the remaining nodes in your cluster.
