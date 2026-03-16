ALTER TABLE personal_ai_memory
DROP CONSTRAINT IF EXISTS personal_ai_memory_memory_kind_check;

ALTER TABLE personal_ai_memory
ADD CONSTRAINT personal_ai_memory_memory_kind_check
CHECK (memory_kind IN ('answer_note', 'web_learning', 'manual_note', 'uploaded_document'));
