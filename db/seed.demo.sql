-- =====================================================================
--  دوائي (Dawaii) — demo data (SQLite): ~20 sample medicines with Arabic
--  names, starter stock batches, and two sample customers.
--  Applied ONCE on a brand-new database by DatabaseInitializer (tracked by
--  the 'demo_seeded' settings flag). Never re-run: deleted items must stay
--  deleted, and the batch inserts below would duplicate stock if repeated.
-- =====================================================================

-- ---------- Sample medicines (prices per SINGLE unit, D-01) ----------
INSERT OR IGNORE INTO items
  (id,name_en,generic_name,units_per_strip,strips_per_box,purchase_price,selling_price,min_quantity) VALUES
  (1 ,'Panadol',   'Paracetamol 500mg',10,10,0.80,1.04,100),
  (2 ,'Brufen',    'Ibuprofen 400mg',10,10,1.20,1.56,60),
  (3 ,'Aspirin',   'Acetylsalicylic 300mg',10,10,0.50,0.65,50),
  (4 ,'Cataflam',  'Diclofenac K 50mg',10,2 ,2.50,3.25,30),
  (5 ,'Amoxil',    'Amoxicillin 500mg',10,2 ,3.00, 4.5,40),
  (6 ,'Zithromax', 'Azithromycin 500mg',3 ,1 ,20.0,  30,10),
  (7 ,'Cipro',     'Ciprofloxacin 500mg',10,1 ,4.00,   6,20),
  (8 ,'Flagyl',    'Metronidazole 500mg',10,2 ,1.50,2.25,30),
  (9 ,'Augmentin', 'Amox+Clav 1g',7 ,2 ,8.00,  12,15),
  (10,'Claritine', 'Loratadine 10mg',10,1 ,3.00, 3.9,20),
  (11,'Congestal', 'Cold tablets',12,1 ,1.00, 1.3,25),
  (12,'Strepsils', 'Throat lozenges',8 ,1 ,1.00, 1.3,30),
  (13,'Antinal',   'Nifuroxazide 200mg',12,1 ,2.00, 2.6,20),
  (14,'Maalox',    'Antacid suspension',1 ,1 ,25.0,32.5,10),
  (15,'Buscopan',  'Hyoscine 10mg',10,1 ,3.00, 3.9,15),
  (16,'Motilium',  'Domperidone 10mg',10,1 ,2.00, 2.6,20),
  (17,'Vitamin C', 'Ascorbic acid 1000mg',10,1 ,2.00, 2.6,30),
  (18,'Centrum',   'Multivitamin',30,1 ,30.0,  39,8),
  (19,'Calcium',   'Calcium + Vit D3',10,1 ,3.00, 3.9,20),
  (20,'Cotton',    'Medical cotton roll',1 ,1 ,20.0,  24,10);

-- ---------- Starter stock batches (single units; FEFO by expiry) ----------
INSERT OR IGNORE INTO stock_batches (item_id,quantity_units,expiry_date,batch_number,strips_per_box,units_per_strip,box_purchase_price,box_selling_price) VALUES
  (1 ,1000,'2027-06-30','LOT-1001',10,10,80,104),
  (2 , 600,'2027-03-31','LOT-1002',10,10,120,156),
  (3 , 500,'2026-08-31','LOT-1003',10,10,50,65),
  (4 , 200,'2027-01-31','LOT-1004',2,10,50,65),
  (5 , 400,'2026-12-31','LOT-1005',2,10,60,90),
  (6 , 120,'2027-05-31','LOT-1006',1,3,60,90),
  (7 , 300,'2027-02-28','LOT-1007',1,10,40,60),
  (8 , 400,'2026-07-31','LOT-1008',2,10,30,45),
  (9 , 150,'2027-04-30','LOT-1009',2,7,112,168),
  (10, 200,'2027-07-31','LOT-1010',1,10,30,39),
  (11, 300,'2027-01-31','LOT-1011',1,12,12,15.6),
  (12, 240,'2026-06-30','LOT-1012',1,8,8,10.4),
  (13, 240,'2027-03-31','LOT-1013',1,12,24,31.2),
  (14, 100,'2027-08-31','LOT-1014',1,1,25,32.5),
  (15, 200,'2027-05-31','LOT-1015',1,10,30,39),
  (16, 200,'2027-06-30','LOT-1016',1,10,20,26),
  (17, 300,'2027-09-30','LOT-1017',1,10,20,26),
  (18,  80,'2027-10-31','LOT-1018',1,30,900,1170),
  (19, 200,'2027-04-30','LOT-1019',1,10,30,39),
  (20, 150,'2028-01-31','LOT-1020',1,1,20,24);

-- Two sample customers for the debt ledger.
INSERT OR IGNORE INTO customers (id,name,phone,balance) VALUES
  (1,'محمد أحمد','0912345678',0.00),
  (2,'فاطمة علي','0923456789',0.00);
