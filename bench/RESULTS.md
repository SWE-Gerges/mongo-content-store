# Benchmark: numeric `$in` versus stringified ids

Produced by `bench/Content.Bench` on the machine and data described below. Nothing here is a claim about other hardware or data sizes.

- Collection: `bench.hotel_supplier-a`, about 200,000 documents
- Ids requested per query: 100 (random, seed 42); projection `{ _id: 1 }`
- Warm-up runs: 3; measured runs per query: 20
- MongoDB server 8.0.38; .NET 10.0.15; Microsoft Windows 10.0.26300; 12 logical cores

| Query | Docs returned | Min ms | Median ms | p95 ms | Max ms | Keys examined | Docs examined | Plan |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| numeric $in on _id | 100 | 3.49 | 4.80 | 16.07 | 16.07 | 200 | 0 | PROJECTION_COVERED > IXSCAN |
| stringified id via $expr/$toString | 100 | 694.26 | 986.47 | 1704.69 | 1704.69 | 0 | 200,000 | PROJECTION_SIMPLE > COLLSCAN |

Median ratio (stringified / numeric): 205.7x
