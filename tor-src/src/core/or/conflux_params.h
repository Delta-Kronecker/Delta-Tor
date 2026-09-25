/* Copyright (c) 2023, The Tor Project, Inc. */
/* See LICENSE for licensing information */

/**
 * \file conflux_params.h
 * \brief Header file for conflux_params.c.
 **/

#ifndef TOR_CONFLUX_PARAMS_H
#define TOR_CONFLUX_PARAMS_H

#include "core/or/or.h"

/** DeltaTor extension: conflux set-selection policies (ConfluxSetSelection). */
typedef enum conflux_set_select_t {
  CONFLUX_SET_SELECT_FIRST = 0,
  CONFLUX_SET_SELECT_ROUND_ROBIN = 1,
  CONFLUX_SET_SELECT_LEAST_STREAMS = 2,
  CONFLUX_SET_SELECT_FASTEST = 3,
} conflux_set_select_t;

bool conflux_is_enabled(const struct circuit_t *circ);
uint8_t conflux_params_get_max_linked_set(void);
uint8_t conflux_params_get_max_prebuilt(void);
uint8_t conflux_params_get_max_unlinked_leg_retry(void);
uint8_t conflux_params_get_num_legs_set(void);
uint8_t conflux_params_get_max_legs_set(void);
void conflux_params_set_num_legs(uint8_t n);
int conflux_params_get_set_selection(void);
int conflux_params_get_set_rtt_max(void);
int conflux_params_get_set_rtt_pct(void);
uint8_t conflux_params_get_drain_pct(void);
uint8_t conflux_params_get_send_pct(void);
uint32_t conflux_params_get_max_oooq(void);

void conflux_params_new_consensus(const networkstatus_t *ns);

#ifdef TOR_UNIT_TESTS
extern uint32_t max_unlinked_leg_retry;
#endif

#endif /* TOR_CONFLUX_PARAMS_H */

