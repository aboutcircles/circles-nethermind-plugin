select f.payload
from unnest(@cids) _cid
         left join ipfs_files f on f.cid = _cid;
