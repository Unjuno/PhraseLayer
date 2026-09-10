"""Regression: dictionary symbols are data, not display text to normalize."""
import hashlib
import unittest
import extract_ppocr_dictionary as production


class DictionaryWhitespaceContractTests(unittest.TestCase):
    def test_unicode_space_survives_yaml_and_utf8_export(self):
        text = 'PostProcess:\n  character_dict:\n  - A\n  - "\\u3000"\n  - B\n  name: CTCLabelDecode\n'
        tokens = production.extract_tokens(text, {"postprocess_name": "CTCLabelDecode", "use_space_char": True})
        self.assertEqual(tokens, ["A", "\u3000", "B"])
        encoded = production.dictionary_bytes(tokens)
        self.assertEqual(encoded, b"A\n\xe3\x80\x80\nB\n")
        trimmed = production.dictionary_bytes([token.strip() for token in tokens])
        self.assertEqual(len(encoded) - len(trimmed), 3)
        self.assertNotEqual(hashlib.sha256(encoded).digest(), hashlib.sha256(trimmed).digest())

    def test_unicode_space_is_not_the_separately_appended_ascii_space(self):
        text = 'PostProcess:\n  character_dict:\n  - "\\u3000"\n  name: CTCLabelDecode\n'
        tokens = production.extract_tokens(text, {"postprocess_name": "CTCLabelDecode", "use_space_char": True})
        effective = [*tokens, " "]
        self.assertEqual(effective, ["\u3000", " "])
        self.assertNotEqual(effective[0], effective[1])

    def test_empty_token_and_padding_spaces_are_not_trimmed(self):
        self.assertEqual(production.dictionary_bytes(["", "  ", "\u3000"]), b"\n  \n\xe3\x80\x80\n")


if __name__ == "__main__":
    unittest.main()
