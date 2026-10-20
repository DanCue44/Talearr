import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Link from 'Components/Link/Link';
import { sizes } from 'Helpers/Props';
import styles from './NamingOption.css';

class NamingOption extends Component {

  //
  // Listeners

  onPress = () => {
    const {
      token,
      tokenSeparator,
      tokenCase,
      isFullFilename,
      isLiteral,
      onPress
    } = this.props;

    let tokenValue = token;

    // Talearr: literal presets (the Audiobookshelf layout) are inserted exactly as shown.
    if (isLiteral) {
      onPress({ isFullFilename, tokenValue });
      return;
    }

    tokenValue = tokenValue.replace(/ /g, tokenSeparator);

    if (tokenCase === 'lower') {
      tokenValue = token.toLowerCase();
    } else if (tokenCase === 'upper') {
      tokenValue = token.toUpperCase();
    }

    onPress({ isFullFilename, tokenValue });
  };

  //
  // Render
  render() {
    const {
      token,
      tokenSeparator,
      example,
      tokenCase,
      isFullFilename,
      isLiteral,
      size
    } = this.props;

    const separator = isLiteral ? ' ' : tokenSeparator;

    return (
      <Link
        className={classNames(
          styles.option,
          styles[size],
          styles[tokenCase],
          isFullFilename && styles.isFullFilename
        )}
        onPress={this.onPress}
      >
        <div className={styles.token}>
          {token.replace(/ /g, separator)}
        </div>

        <div className={styles.example}>
          {example.replace(/ /g, separator)}
        </div>
      </Link>
    );
  }
}

NamingOption.propTypes = {
  token: PropTypes.string.isRequired,
  example: PropTypes.string.isRequired,
  tokenSeparator: PropTypes.string.isRequired,
  tokenCase: PropTypes.string.isRequired,
  isFullFilename: PropTypes.bool.isRequired,
  isLiteral: PropTypes.bool,
  size: PropTypes.oneOf([sizes.SMALL, sizes.LARGE]),
  onPress: PropTypes.func.isRequired
};

NamingOption.defaultProps = {
  size: sizes.SMALL,
  isFullFilename: false,
  isLiteral: false
};

export default NamingOption;
