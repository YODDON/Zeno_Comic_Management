# Reading module

R01 owns the chapter access decision and manifest guard. `ChapterAccessHandler` applies the documented order: public/valid content first, then Free, active VIP when eligible, durable coin unlock, otherwise Locked. `IChapterAccessProjectionStore` is a batched read port implemented by Persistence; V01 and U02 later provide the production VIP and unlock signals without adding module-to-module references.

Free Chapter manifests contain Ready pages in stable order and use signed authenticated Cloudinary delivery URLs. Paid/VIP delivery remains a Q02 verification gate, so an entitled protected request fails with provider unavailable instead of receiving a Free URL. The API rechecks access for each manifest request and marks successful manifests `no-store`.

R02 sessions/progress/qualification, follows, unlock mutation, VIP persistence and frontend reader behavior are outside R01.
