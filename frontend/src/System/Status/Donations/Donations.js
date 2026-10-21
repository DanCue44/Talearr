import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import Link from 'Components/Link/Link';
import translate from 'Utilities/String/translate';
import styles from '../styles.css';

class Donations extends Component {

  //
  // Render

  render() {
    return (
      <FieldSet legend={translate('DonationsLegend')} id="donations">
        <div className={styles.donationContainer}>
          <div className={styles.donationText}>
            <p>{translate('DonationsIntro')}</p>
          </div>

          <div className={styles.donationButtons}>
            <Link
              to="https://github.com/DanCue44/Talearr"
              className={styles.donationButton}
              title={translate('DonationsGitHubTitle')}
            >
              <div className={styles.buttonContent}>
                <span className={styles.buttonIcon}>{'⭐'}</span>
                <span className={styles.buttonText}>{translate('DonationsGitHubLabel')}</span>
                <span className={styles.buttonSubtext}>{translate('DonationsGitHubSubtext')}</span>
              </div>
            </Link>
          </div>

          <div className={styles.donationFooter}>
            <p>
              <small>
                {translate('DonationsFooter')}
                {' '}
                <Link to="https://ko-fi.com/chaptarr" title={translate('DonationsKofiTitle')}>
                  {translate('DonationsKofiLabel')}
                </Link>
              </small>
            </p>
          </div>
        </div>
      </FieldSet>
    );
  }
}

Donations.propTypes = {

};

export default Donations;
