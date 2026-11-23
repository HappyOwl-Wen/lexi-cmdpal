"""Run with: python -m unittest discover -s tests -p test_british_recordings.py"""
import importlib.util
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

spec = importlib.util.spec_from_file_location('recordings', Path(__file__).parents[1] / 'scripts/import-british-recordings.py')
recordings = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recordings)


class BritishRecordingImportTests(unittest.TestCase):
    def test_filename_match_is_exact_and_language_specific(self):
        self.assertEqual('well-being', recordings.headword('File:LL-Q1860 (eng)-Vealhurl-well-being.wav', 'Vealhurl'))
        self.assertEqual('read2', recordings.headword('File:LL-Q1860 (eng)-Vealhurl-read2.wav', 'Vealhurl'))
        self.assertIsNone(recordings.headword('File:LL-Q150 (fra)-Vealhurl-chat.wav', 'Vealhurl'))
        self.assertIsNone(recordings.headword('File:LL-Q1860 (eng)-Other-test.wav', 'Vealhurl'))

    def test_backup_additive_import_and_repeat(self):
        with tempfile.TemporaryDirectory() as folder:
            db = sqlite3.connect(':memory:')
            db.executescript('''
                CREATE TABLE sources(id INTEGER PRIMARY KEY, code TEXT UNIQUE, name TEXT,
                  license TEXT,url TEXT,version TEXT,attribution TEXT,redistributable INTEGER);
                CREATE TABLE words(id INTEGER PRIMARY KEY,word TEXT,lang INTEGER);
                CREATE TABLE entries(word_id INTEGER,source_id INTEGER,seq INTEGER,pos TEXT,data BLOB,
                  PRIMARY KEY(word_id,source_id,seq));
                INSERT INTO sources(id,code) VALUES(1,'original');
                INSERT INTO words VALUES(1,'example',1),(2,'example',2);
            ''')
            original = recordings.encode({'h': 'example', 's': [{'gl': 'original definition'}],
                                          'au': [{'f': 'En-us-example.ogg', 'r': 'US'}]})
            db.execute('INSERT INTO entries VALUES(1,1,0,NULL,?)', (original,))
            db.commit()
            item = dict(word_id=1, word='example', speaker='Back ache', license='CC BY-SA 4.0',
                        file='LL-Q1860 (eng)-Back ache-example.wav')
            report = {'recordings': [item]}
            backup = Path(folder) / 'before.db'
            recordings.apply(db, report, backup)
            with closing(sqlite3.connect(backup)) as saved:
                self.assertEqual(1, saved.execute('SELECT count(*) FROM entries').fetchone()[0])
                self.assertEqual(original, saved.execute('SELECT data FROM entries').fetchone()[0])
            recordings.apply(db, report, Path(folder) / 'second.db')
            self.assertEqual(2, db.execute('SELECT count(*) FROM entries').fetchone()[0])
            self.assertEqual(original, db.execute('SELECT data FROM entries WHERE source_id=1').fetchone()[0])
            words, audio, british = recordings.inventory(db)
            self.assertEqual({'example': 1}, words)
            self.assertEqual({1}, british)
            self.assertEqual(2, len(audio[1]))
            blob = db.execute('SELECT data FROM entries WHERE source_id<>1').fetchone()[0]
            self.assertEqual(1, len(recordings.decode(blob)['au']))
            with self.assertRaises(FileExistsError):
                recordings.apply(db, report, backup)
            db.close()

    def test_corrupt_codec_is_rejected(self):
        with self.assertRaises(ValueError):
            recordings.decode(b'bad')
        with self.assertRaises(ValueError):
            recordings.decode(b'\x00\x01\x00\x00\x00{}')


if __name__ == '__main__':
    unittest.main()
