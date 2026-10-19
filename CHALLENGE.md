A traditional e-commerce company will start operating as a marketplace (a store that sells
products from other stores). Marketplaces frequently receive product catalogs from multiple
sellers (stores that sell their products on the marketplace).
Currently, this company has a product catalog that contains all the items it sells. The store must
be capable of receiving products from different sellers and adding them to its catalog. It is
important to note that it is common for a product to be sold by several stores. Each seller registers their own products, so there may be slight variations in the information for the same
item across different stores. Duplicating items is undesirable. However, it is crucial to record
which sellers offer each product.
You will receive a SQLite database populated with products. This database has only 2 tables: a
product table and a table to link products to sellers.
Implement a catalog consolidation system that receives a file containing products from
different sellers and saves them to the catalog database. In the case of a duplicate product, the
system should not insert the item into the product table but should link the existing item to the
seller in the appropriate table.
Demonstrating mastery of the problem and understanding of the solution is more important than
presenting a scalable, production-ready implementation.
This challenge contains some intentional ambiguities. They are part of the assessment.
You may make changes to the database if you deem them necessary.
