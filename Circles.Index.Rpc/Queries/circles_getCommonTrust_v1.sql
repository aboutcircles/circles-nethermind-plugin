-- v1Sql
select trustee
from "V_Crc_TrustRelations"
where truster in (@address1, @address2)
  and trustee not in (@address1, @address2)
  and version = 1
group by trustee
having count(truster) > 1;
