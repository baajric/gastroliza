using Xunit;

// Svi testovi koji gađaju bazu dijele jedan SQL Server i jednu bazu Analitika.
// Kad su išli usporedo, priprema baze (CREATE DATABASE, CREATE INDEX) zaključavala
// je sys.databases, pa su čitanja u drugom razredu čekala do isteka roka i padala.
// Paket je mali; serijsko izvršavanje košta nekoliko sekundi, a uklanja lažne padove.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
