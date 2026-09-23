----break----

-- Ticks exported by the trade server: the top of the order book of one symbol, one row per change of the best
-- bid or the best ask. The definition follows the ticks script of the Export Setup guide column for column;
-- only the table name, the cluster clause and the engine are templated.
--
-- Ticks are the one entity the trade server exports to ClickHouse, and this is the first table the ClickHouse
-- folder creates itself. There is no cursor table: a ClickHouse pipeline keeps its position on disk.
--
-- Deduplication: after a lost connection the server re-sends the batch it could not confirm, so the same rows
-- can arrive twice. ReplacingMergeTree collapses them on the sorting key when the parts merge; until then a
-- query returns both copies unless it reads with FINAL. The duplicates are identical, so no version column is
-- needed.
--
-- The primary key is the sparse index ClickHouse holds in memory, so it stops at the symbol; the sorting key
-- goes on to created_time and sequence so that two ticks of one symbol never collapse into each other.
--
-- Retention: the TTL is not part of the definition. post/ticks_ttl.sql sets it from ##ticks_ttl_days## on every
-- run whose value differs from the last one, so a deployment changes it with --variables rather than by hand.
-- Once a TTL is set, a part is dropped as a whole when every row in it has expired (ttl_only_drop_parts), which
-- the monthly partitioning makes cheap.
--
-- The name is qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE TABLE IF NOT EXISTS ##database_name##.##ticks_table## ##cluster_name_suffix##
(
    -- Service columns
    sender_id                              UInt64                      NOT NULL,
    sequence                               UInt64                      NOT NULL,
    batch_time                             DateTime64(6, 'UTC')        NOT NULL CODEC(Delta, Default),

    created_time                           DateTime64(6, 'UTC')        NOT NULL CODEC(Delta, Default),

    symbol_id                              UInt64                      NOT NULL,
    short_symbol_name                      LowCardinality(String)      NOT NULL,

    -- best bid (zero volume means the side is empty)
    bid_price                              Decimal128(12)              NOT NULL,
    bid_volume                             Decimal64(8)                NOT NULL,

    -- best ask (zero volume means the side is empty)
    ask_price                              Decimal128(12)              NOT NULL,
    ask_volume                             Decimal64(8)                NOT NULL
)
ENGINE = ##replacing_merge_tree_engine##
PARTITION BY toYYYYMM(created_time)
PRIMARY KEY (sender_id, short_symbol_name)
ORDER BY (sender_id, short_symbol_name, created_time, sequence)
SETTINGS ttl_only_drop_parts = 1;
