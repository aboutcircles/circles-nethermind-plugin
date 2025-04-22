CREATE OR REPLACE FUNCTION crc_day("inflationDayZero" bigint, "timestamp" bigint)
RETURNS bigint AS $$
DECLARE
    DEMURRAGE_WINDOW bigint := 86400;
BEGIN
    RETURN ("timestamp" - "inflationDayZero") / DEMURRAGE_WINDOW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION crc_demurrage("inflationDayZero" bigint, "timestamp" bigint, "value" numeric)
    RETURNS numeric AS $$
DECLARE
    _day_last_interaction bigint;
    _now bigint := EXTRACT(EPOCH FROM NOW())::bigint;
    _day_now bigint;
    _gamma numeric := 0.9998013320085989574306481700129226782902039065082930593676448873;
BEGIN
    _day_last_interaction := crc_day("inflationDayZero", "timestamp");
    _day_now := crc_day("inflationDayZero", _now);
    return (value * POWER(_gamma, _day_now - _day_last_interaction));
END;
$$ LANGUAGE plpgsql;

