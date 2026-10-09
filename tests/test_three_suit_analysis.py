"""Check the new 27-card rules separately from the historical game engine."""

from itertools import combinations
import random
import unittest

from simulation.three_suit_analysis import DECK, best_score, layout_masks, legal_triples, triple_score


def card(suit: int, rank: int) -> int:
    return suit * 9 + rank - 1


class ThreeSuitTests(unittest.TestCase):
    def test_both_private_cards_can_complete_trips(self):
        hand = (card(0, 4), card(1, 4))
        board = (card(2, 4), card(0, 7), card(1, 9))
        self.assertEqual(best_score(hand, board), (5, 4))

    def test_public_only_trips_cannot_be_used(self):
        hand = (card(0, 7), card(1, 8))
        board = (card(0, 4), card(1, 4), card(2, 4))
        self.assertEqual(best_score(hand, board), (1, 4, 8))

    def test_known_combinations_expand_as_one_four_nine(self):
        hand = (card(0, 4), card(0, 5))
        board = (card(0, 6), card(1, 9), card(2, 2))
        self.assertEqual([len(legal_triples(hand, board[:n])) for n in (1, 2, 3)], [1, 4, 9])
        self.assertEqual(best_score(hand, board), (4, 6))

    def test_rarity_order_and_tie_breaks(self):
        trips = tuple(card(suit, 1) for suit in range(3))
        straight_flush = tuple(card(0, rank) for rank in (7, 8, 9))
        straight = (card(0, 1), card(1, 2), card(2, 3))
        flush = tuple(card(0, rank) for rank in (4, 8, 9))
        self.assertGreater(triple_score(trips), triple_score(straight_flush))
        self.assertGreater(triple_score(straight_flush), triple_score(straight))
        self.assertGreater(triple_score(straight), triple_score(flush))
        self.assertEqual(triple_score(tuple(card(0, rank) for rank in (8, 9, 1)))[0], 2)
        self.assertGreater(triple_score((card(0, 4), card(1, 4), card(2, 9))), triple_score((card(0, 4), card(1, 4), card(2, 8))))

    def test_fast_enumerator_matches_explicit_private_public_partitions(self):
        lookup = {triple: triple_score(triple)[0] for triple in combinations(DECK, 3)}
        rng = random.Random(27)
        for _ in range(100):
            cards = tuple(sorted(rng.sample(DECK, 5)))
            explicit = []
            for board in combinations(cards, 3):
                hand = tuple(card for card in cards if card not in board)
                categories = {triple_score(triple)[0] for triple in legal_triples(hand, board)}
                explicit.append(sum(1 << category for category in categories))
            self.assertEqual(layout_masks(cards, lookup), explicit)


if __name__ == '__main__':
    unittest.main()
